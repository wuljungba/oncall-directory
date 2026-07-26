/**
 * dispatch-service.js
 * ---------------------------------------------------------------
 * Reference backend for automating hospital code-call alerts through:
 *   1. Singlewire InformaCast   (REST API broadcast trigger — overhead
 *                                speakers, desk phones, mobile push)
 *   2. Vocera                   (REST messaging API — badges / Vocera
 *                                Smartphone app, group-based, with ack)
 *   3. Cisco CUCM AXL           (SOAP config/query API — pre-flight
 *                                device checks + on-call number lookup,
 *                                NOT used for real-time origination —
 *                                see note below)
 *   4. Direct SIP paging        (last-resort fallback if both InformaCast
 *                                and Vocera fail)
 *
 * IMPORTANT ON AXL'S ROLE: Cisco's AXL API is a provisioning/config
 * interface (SOAP), not a real-time call-control API. It's the right
 * tool for "is this phone registered," "what's the on-call extension
 * right now," or "update this paging group's membership" — but it
 * cannot itself originate a page or a broadcast. Real-time origination
 * for CUCM-connected endpoints happens through InformaCast (which
 * integrates with CUCM over JTAPI/CTI internally) or through CUCM's
 * own CTI/AXL-adjacent real-time API (RisPort70) for status, not AXL.
 * This file uses AXL for verification and lookups, and leaves actual
 * page origination to InformaCast/Vocera/the SIP fallback.
 *
 * This is a design reference, not a drop-in production file — plug in
 * your real CUCM host, AXL credentials, Vocera tenant, InformaCast
 * tenant, and group/template IDs before deploying. Dependencies:
 * `npm install express node-fetch soap`
 * ---------------------------------------------------------------
 */

const express = require('express');
const fetch = require('node-fetch');
const soap = require('soap');
const crypto = require('crypto');

const app = express();
app.use(express.json());

// ---------------------------------------------------------------
// Configuration — pull these from environment/secrets manager,
// never hardcode in source.
// ---------------------------------------------------------------
const CONFIG = {
  informacast: {
    baseUrl: process.env.IC_BASE_URL,
    apiToken: process.env.IC_API_TOKEN,
    broadcastTemplateIds: {
      medical: 'bpt_medical_emergency',
      rrt: 'bpt_rapid_response',
      fire: 'bpt_fire',
      infant: 'bpt_infant_abduction',
      behavior: 'bpt_behavioral_emergency',
      threat: 'bpt_active_threat',
    },
  },

  vocera: {
    baseUrl: process.env.VOCERA_BASE_URL,     // e.g. https://vocera.hospital.local/vmp/api/v1
    apiKey: process.env.VOCERA_API_KEY,
    // Vocera groups configured in the Vocera Voice Server / Platform
    // admin console — badges and Vocera Smartphone app users who are
    // members receive the alert with the given priority.
    groupIds: {
      medical: 'grp_code_team',
      rrt: 'grp_rrt_team',
      fire: 'grp_fire_safety',
      infant: 'grp_security_peds',
      behavior: 'grp_security',
      threat: 'grp_security_admin',
    },
  },

  cucm: {
    host: process.env.CUCM_HOST,              // e.g. cucm-pub.hospital.local
    axlUser: process.env.CUCM_AXL_USER,
    axlPassword: process.env.CUCM_AXL_PASSWORD,
    // WSDL shipped with your CUCM version, e.g.
    // https://<cucm-host>:8443/axl/schema/<version>/AXLAPI.wsdl
    axlWsdlUrl: process.env.CUCM_AXL_WSDL,
    // Device names (as configured in CUCM) for the overhead paging
    // endpoints you want verified before a page goes out.
    pagingDevicesByLocation: {
      '3 West — Room 312': ['SEP-PAGE-3W-01'],
      'ICU — Bay 4': ['SEP-PAGE-ICU-04'],
      'Emergency Dept — Trauma 2': ['SEP-PAGE-ED-02'],
      'Main Lobby': ['SEP-PAGE-LOBBY-01'],
    },
  },

  sipFallback: {
    pbxHost: process.env.SIP_PBX_HOST,
    trunkUser: process.env.SIP_TRUNK_USER,
    trunkSecret: process.env.SIP_TRUNK_SECRET,
    pagingZones: {
      '3-west': 'sip:page-3west@pbx.hospital.local',
      'icu': 'sip:page-icu@pbx.hospital.local',
      'ed': 'sip:page-ed@pbx.hospital.local',
      'lobby': 'sip:page-lobby@pbx.hospital.local',
    },
  },

  webhookSigningSecret: process.env.IC_WEBHOOK_SECRET,
  voceraWebhookSigningSecret: process.env.VOCERA_WEBHOOK_SECRET,
};

// In-memory store for the reference implementation.
// Replace with your real datastore (Postgres, etc.) in production.
const incidents = new Map();

// =================================================================
// 1. Incident activation — called by the front-end app
// =================================================================
app.post('/api/incidents', async (req, res) => {
  const { codeType, location, notes, activatedBy } = req.body;

  if (!CONFIG.informacast.broadcastTemplateIds[codeType]) {
    return res.status(400).json({ error: `Unknown code type: ${codeType}` });
  }

  const incidentId = crypto.randomUUID();
  const incident = {
    id: incidentId,
    codeType,
    location,
    notes,
    activatedBy,
    createdAt: new Date().toISOString(),
    status: 'dispatching',
    steps: [{ step: 'created', at: new Date().toISOString() }],
  };
  incidents.set(incidentId, incident);

  // Best-effort pre-flight check against CUCM — never blocks dispatch,
  // just gets logged so telecom can see stale/unregistered devices.
  await preflightCheckDevices(incident);

  // Fire InformaCast and Vocera in parallel — most hospitals run both
  // an overhead/desk-phone channel and a badge/smartphone channel
  // simultaneously rather than choosing one.
  const [icResult, voceraResult] = await Promise.allSettled([
    triggerInformaCastBroadcast(incident),
    sendVoceraAlert(incident),
  ]);

  if (icResult.status === 'fulfilled') {
    incident.steps.push({ step: 'informacast_triggered', at: new Date().toISOString() });
  } else {
    incident.steps.push({ step: 'informacast_failed', at: new Date().toISOString(), error: icResult.reason.message });
  }

  if (voceraResult.status === 'fulfilled') {
    incident.steps.push({ step: 'vocera_triggered', at: new Date().toISOString() });
  } else {
    incident.steps.push({ step: 'vocera_failed', at: new Date().toISOString(), error: voceraResult.reason.message });
  }

  if (icResult.status === 'rejected' && voceraResult.status === 'rejected') {
    // Both primary channels failed — fall back to a direct SIP page so
    // overhead announcement still fires while you investigate.
    try {
      await sendDirectSipPage(incident);
      incident.steps.push({ step: 'sip_fallback_sent', at: new Date().toISOString() });
      incident.status = 'dispatched_via_fallback';
    } catch (sipErr) {
      incident.status = 'dispatch_failed';
      incident.steps.push({ step: 'sip_fallback_failed', at: new Date().toISOString(), error: sipErr.message });
      notifyTelecomOnCall(incident);
    }
  } else {
    incident.status = 'dispatched';
  }

  res.status(201).json(incident);
});

// =================================================================
// 2. Cisco CUCM AXL — pre-flight device check + on-call lookup
// =================================================================
let axlClientPromise = null;
function getAxlClient() {
  if (!axlClientPromise) {
    axlClientPromise = soap
      .createClientAsync(CONFIG.cucm.axlWsdlUrl, {
        wsdl_options: { rejectUnauthorized: false }, // internal CUCM CA — verify properly in prod
      })
      .then((client) => {
        client.setSecurity(new soap.BasicAuthSecurity(CONFIG.cucm.axlUser, CONFIG.cucm.axlPassword));
        client.setEndpoint(`https://${CONFIG.cucm.host}:8443/axl/`);
        return client;
      });
  }
  return axlClientPromise;
}

async function preflightCheckDevices(incident) {
  const deviceNames = CONFIG.cucm.pagingDevicesByLocation[incident.location];
  if (!deviceNames || deviceNames.length === 0) {
    incident.steps.push({ step: 'cucm_check_skipped', at: new Date().toISOString(), detail: 'no devices mapped for location' });
    return;
  }
  try {
    const rows = await verifyDevicesRegistered(deviceNames);
    const unregistered = deviceNames.filter((name) => !rows.some((r) => r.name === name));
    incident.steps.push({
      step: 'cucm_checked',
      at: new Date().toISOString(),
      detail: unregistered.length ? `unregistered: ${unregistered.join(', ')}` : 'all devices found',
    });
  } catch (err) {
    // Never block dispatch on an AXL failure — just log it.
    incident.steps.push({ step: 'cucm_check_failed', at: new Date().toISOString(), error: err.message });
  }
}

async function verifyDevicesRegistered(deviceNames) {
  const client = await getAxlClient();
  const sql = `SELECT d.name, d.description, tm.name AS model
               FROM device d
               JOIN typemodel tm ON tm.enum = d.tkmodel
               WHERE d.name IN (${deviceNames.map((n) => `'${n.replace(/'/g, "''")}'`).join(',')})`;
  const [result] = await client.executeSQLQueryAsync({ sql });
  const rows = result?.return?.row || [];
  return Array.isArray(rows) ? rows : [rows];
}

// Example: resolve the current on-call number for a role by reading a
// device/line description convention your telecom team maintains
// (e.g. house supervisors update this when logging on-call staff in/out).
async function resolveOnCallExtension(role) {
  const client = await getAxlClient();
  const sql = `SELECT n.dnorpattern
               FROM numplan n
               JOIN devicenumplanmap m ON m.fknumplan = n.pkid
               JOIN device d ON d.pkid = m.fkdevice
               WHERE d.description = 'ONCALL_${role.toUpperCase().replace(/'/g, "")}'`;
  const [result] = await client.executeSQLQueryAsync({ sql });
  const row = result?.return?.row;
  if (!row) return null;
  return Array.isArray(row) ? row[0].dnorpattern : row.dnorpattern;
}

// =================================================================
// 3. InformaCast broadcast trigger
// =================================================================
async function triggerInformaCastBroadcast(incident) {
  const templateId = CONFIG.informacast.broadcastTemplateIds[incident.codeType];

  const response = await fetch(`${CONFIG.informacast.baseUrl}/broadcasts/trigger`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${CONFIG.informacast.apiToken}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      templateId,
      variables: {
        location: incident.location,
        notes: incident.notes || '',
        incidentId: incident.id,
      },
      callbackUrl: `${process.env.PUBLIC_APP_URL}/webhooks/informacast`,
    }),
    timeout: 5000,
  });

  if (!response.ok) throw new Error(`InformaCast API returned ${response.status}`);
  return response.json();
}

// =================================================================
// 4. Vocera messaging trigger
// =================================================================
async function sendVoceraAlert(incident) {
  const groupId = CONFIG.vocera.groupIds[incident.codeType];
  if (!groupId) throw new Error(`No Vocera group mapped for code type: ${incident.codeType}`);

  const response = await fetch(`${CONFIG.vocera.baseUrl}/messages`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${CONFIG.vocera.apiKey}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      recipientGroupId: groupId,
      priority: incident.codeType === 'threat' ? 'urgent' : 'high',
      text: `${incident.codeType.toUpperCase()} — ${incident.location}${incident.notes ? ' — ' + incident.notes : ''}`,
      requireAcknowledgment: true,
      callbackUrl: `${process.env.PUBLIC_APP_URL}/webhooks/vocera`,
      incidentId: incident.id,
    }),
    timeout: 5000,
  });

  if (!response.ok) throw new Error(`Vocera API returned ${response.status}`);
  return response.json();
}

// =================================================================
// 5. Direct SIP paging fallback (last resort — see prior note on
//    using a real SIP stack / AMI-ARI rather than hand-rolled SIP)
// =================================================================
async function sendDirectSipPage(incident) {
  const zoneUri = CONFIG.sipFallback.pagingZones[normalizeZoneKey(incident.location)];
  if (!zoneUri) throw new Error(`No SIP paging zone mapped for location: ${incident.location}`);

  const response = await fetch(`http://${CONFIG.sipFallback.pbxHost}/ari/channels`, {
    method: 'POST',
    headers: {
      Authorization:
        'Basic ' + Buffer.from(`${CONFIG.sipFallback.trunkUser}:${CONFIG.sipFallback.trunkSecret}`).toString('base64'),
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      endpoint: zoneUri,
      app: 'code-call-paging',
      appArgs: `announce=${encodeURIComponent(incident.codeType + ' ' + incident.location)}`,
    }),
    timeout: 5000,
  });

  if (!response.ok) throw new Error(`SIP/ARI paging call returned ${response.status}`);
  return response.json();
}

function normalizeZoneKey(location) {
  return location.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
}

// =================================================================
// 6. Webhooks — InformaCast and Vocera acknowledgment/delivery events
// =================================================================
app.post('/webhooks/informacast', express.raw({ type: '*/*' }), (req, res) => {
  handleChannelWebhook(req, res, CONFIG.webhookSigningSecret, 'x-informacast-signature');
});

app.post('/webhooks/vocera', express.raw({ type: '*/*' }), (req, res) => {
  handleChannelWebhook(req, res, CONFIG.voceraWebhookSigningSecret, 'x-vocera-signature');
});

function handleChannelWebhook(req, res, secret, signatureHeader) {
  const signature = req.headers[signatureHeader];
  const expected = crypto.createHmac('sha256', secret).update(req.body).digest('hex');

  if (signature !== expected) {
    return res.status(401).json({ error: 'invalid signature' });
  }

  const payload = JSON.parse(req.body.toString());
  const incident = incidents.get(payload.incidentId);
  if (!incident) return res.status(404).json({ error: 'unknown incident' });

  incident.steps.push({
    step: payload.eventType, // e.g. 'delivered_to_speaker', 'acknowledged', 'failed'
    at: new Date().toISOString(),
    detail: payload.detail,
  });

  if (payload.eventType === 'acknowledged') {
    incident.status = 'acknowledged';
  }

  // Push the update to connected dashboard clients over WebSocket here.
  // broadcastToClients(incident);

  res.status(200).json({ received: true });
}

function notifyTelecomOnCall(incident) {
  console.error(`[CRITICAL] All dispatch paths failed for incident ${incident.id}. Manual page required.`);
}

// ---------------------------------------------------------------
app.listen(process.env.PORT || 4000, () => {
  console.log('Code call dispatch service listening');
});

module.exports = {
  app,
  triggerInformaCastBroadcast,
  sendVoceraAlert,
  sendDirectSipPage,
  verifyDevicesRegistered,
  resolveOnCallExtension,
};
