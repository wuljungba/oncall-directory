# Twilio SMS Dispatch — Setup Runbook

Twilio is the SMS channel of the code-call dispatch pipeline: when a code call fires, the
current primary on-call provider for the event's department gets a text on the mobile
number in their directory record.

This is a **safety-critical path**. Everything below exists so that a message that does not
reach the provider shows up as a failed step, not a green one.

> **Credentials are never committed and never stored in the application database.** They
> live in App Service configuration, with the Auth Token behind a Key Vault reference. The
> Admin UI can test the channel but cannot read or write the credentials.

---

## 1. What you need from Twilio

| Item | Where to find it | Config key |
|------|------------------|------------|
| Account SID (`AC…`) | Twilio Console → Account Info | `Dispatch__Twilio__AccountSid` |
| Auth Token | Twilio Console → Account Info | Key Vault secret `TwilioAuthToken` |
| Sending number (E.164, e.g. `+12025551234`) | Phone Numbers → Manage → Active numbers | `Dispatch__Twilio__FromNumber` |
| *or* Messaging Service SID (`MG…`) | Messaging → Services | `Dispatch__Twilio__MessagingServiceSid` |

Prefer the **Messaging Service** for US traffic — see the A2P note in §5.

Trial accounts can only send to numbers you have verified in the Twilio console
(Phone Numbers → Verified Caller IDs). Verify your own handset before testing.

---

## 2. Store the Auth Token in Key Vault

Run this yourself — replace `<vault>` with the vault name from the deployment output
(`keyVaultName`) and paste your token when prompted rather than putting it in shell history:

```bash
read -rs TWILIO_TOKEN && az keyvault secret set --vault-name <vault> --name TwilioAuthToken --value "$TWILIO_TOKEN" && unset TWILIO_TOKEN
```

The web app and staging slot already hold the **Key Vault Secrets User** role, so the
`@Microsoft.KeyVault(...)` reference in `infrastructure/bicep/main.bicep` resolves without
further grants.

---

## 3. Set the remaining values

Either redeploy the Bicep template with the Twilio parameters:

```bash
az deployment group create --resource-group rg-oncall-production --template-file infrastructure/bicep/main.bicep --parameters environmentName=production twilioEnabled=true twilioAccountSid=AC... twilioFromNumber=+12025551234
```

…or set them directly on the running app (production and staging are separate — do both):

```bash
az webapp config appsettings set --name app-oncall-production --resource-group rg-oncall-production --settings Dispatch__Twilio__Enabled=true Dispatch__Twilio__AccountSid=AC... Dispatch__Twilio__FromNumber=+12025551234 Dispatch__Twilio__StatusCallbackUrl=https://app-oncall-production.azurewebsites.net/api/public/twilio/status
```

```bash
az webapp config appsettings set --name app-oncall-production --slot staging --resource-group rg-oncall-production --settings Dispatch__Twilio__Enabled=true Dispatch__Twilio__AccountSid=AC... Dispatch__Twilio__FromNumber=+12025551234 Dispatch__Twilio__StatusCallbackUrl=https://app-oncall-production-staging.azurewebsites.net/api/public/twilio/status
```

> A Bicep redeploy replaces the whole app-settings collection. Any setting applied only
> with `az webapp config appsettings set` (including `GraphApi__*`) is dropped by the next
> template deployment — re-apply those after deploying, or move them into the template.

**Startup refuses to run** in production if the channel is enabled with placeholder
credentials, with a non-E.164 `FromNumber`, or with neither a from number nor a messaging
service (`Program.cs`). Missing `StatusCallbackUrl` logs a loud warning rather than failing,
because it degrades detection rather than sending.

---

## 4. Delivery status callback

`POST /api/public/twilio/status` receives Twilio's delivery updates.

- It is anonymous by route but authenticated by Twilio's `X-Twilio-Signature`, an HMAC keyed
  by your Auth Token. Requests failing verification get a 403 and are logged.
- The signature is computed over the URL Twilio was given, so `StatusCallbackUrl` must match
  the app's public URL **exactly** (scheme, host, path).
- `delivered` settles the `twilio_sms` step as completed. `undelivered`, `failed`, and
  `canceled` flip it to **failed**, log at Error, and push the failure to the Command Center
  over SignalR.
- Nothing needs to be configured in the Twilio console: the callback URL is attached to each
  message at send time.

---

## 5. A2P 10DLC (US traffic) — read before going live

Sending SMS to US numbers from a standard 10-digit long code requires a registered A2P
brand and campaign (Twilio Console → Messaging → Regulatory Compliance). Unregistered
traffic is **filtered by carriers** — Twilio accepts the message, then delivery fails or
silently drops.

For a code call that means the alert never arrives. The status callback makes the failure
visible, but it does not prevent it. Register the campaign, attach the number to a Messaging
Service, and set `Dispatch__Twilio__MessagingServiceSid` before relying on this channel.

Toll-free numbers require verification instead; short codes do not require 10DLC.

---

## 6. Testing locally

`scripts/run-local-backend.sh` enables the channel only when you export the credentials
yourself, so nothing secret is committed:

```bash
export TWILIO_ACCOUNT_SID='AC...'
read -rs TWILIO_AUTH_TOKEN && export TWILIO_AUTH_TOKEN
export TWILIO_FROM_NUMBER='+1...'
./scripts/run-local-backend.sh
```

`read -rs` keeps the token out of shell history. The script prints `Twilio SMS: ENABLED` at
startup; without `TWILIO_AUTH_TOKEN` it prints `disabled` and behaves as before.

Then Admin → *Test connection* and *Send test SMS*, as in §7.

**The delivery callback cannot be tested locally.** Twilio has to reach
`StatusCallbackUrl` from the public internet and `localhost` is unreachable, so the outbound
send is all that is verifiable here — a send reports `queued` and never settles. To exercise
the callback, run a tunnel and set `TWILIO_STATUS_CALLBACK_URL` to
`<tunnel-url>/api/public/twilio/status`; the signature is computed over that exact URL, so it
must match what Twilio actually posts to.

---

## 7. Verify

1. **Credentials** — Admin → Settings → Code Call Dispatch Integration → *Test connection*.
   Expect "Twilio account reachable".
2. **End-to-end send** — same panel, *Send test SMS* to your own (verified, on a trial
   account) handset. The message is prefixed `[TEST]` and the send is written to the audit
   log.
3. **Real pipeline, on staging** — trigger a code call against a staging phone tree whose
   on-call provider has a mobile number on file. The Command Center pipeline should show
   **SMS to On-Call**, first as sent, then settling to delivered via the callback.
4. **Failure surfaces** — repeat with a provider whose mobile number is invalid or absent.
   The step must show as **failed**, never skipped or green.

Production only after staging passes all four.

---

## 8. Troubleshooting

| Symptom | Cause |
|---------|-------|
| App fails to start after enabling | Placeholder credentials, or `FromNumber` not in E.164 — the startup guard is deliberate |
| Test connection returns 401 | Account SID / Auth Token mismatch, or the Key Vault reference did not resolve (check the app's Key Vault Secrets User role) |
| Test connection returns 400 "Missing required header Twilio-Api-Version" | The request lost the `/2010-04-01` path segment — `TwilioClient.ApiBase` must keep its trailing slash, or URI resolution drops it |
| Send returns Twilio code 21608 | Trial account sending to an unverified number |
| Send returns Twilio code 21606 | `FromNumber` is not a number you own, or is not SMS-capable |
| Step stays "sent", never settles | `StatusCallbackUrl` unset or does not match the public URL |
| Status callbacks return 403 | Signature mismatch — usually `StatusCallbackUrl` differing from the real request URL |
| Step is `failed` with "no mobile number on file" | The on-call provider's `Employee.MobilePhone` is blank — see `docs/onboarding-standard.md` |
