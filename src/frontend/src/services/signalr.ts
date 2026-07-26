import {
  HubConnectionBuilder,
  type HubConnection,
  LogLevel,
  HubConnectionState,
} from '@microsoft/signalr'

let connection: HubConnection | null = null

export type NotificationEvent =
  | { type: 'ScheduleCreated'; payload: unknown }
  | { type: 'ScheduleUpdated'; payload: unknown }
  | { type: 'ScheduleDeleted'; payload: unknown }
  | { type: 'ShiftAssigned'; payload: unknown }
  | { type: 'ShiftsGenerated'; payload: unknown }
  | { type: 'SwapRequested'; payload: unknown }
  | { type: 'SwapApproved'; payload: unknown }
  | { type: 'TimeOffUpdated'; payload: unknown }
  | { type: 'EmployeeCreated'; payload: unknown }
  | { type: 'EmployeeUpdated'; payload: unknown }
  | { type: 'EmployeeDeactivated'; payload: unknown }
  | { type: 'DepartmentCreated'; payload: unknown }
  | { type: 'DepartmentUpdated'; payload: unknown }
  | { type: 'DepartmentDeactivated'; payload: unknown }
  | { type: 'PhoneTreeCreated'; payload: unknown }
  | { type: 'PhoneTreeUpdated'; payload: unknown }
  | { type: 'PhoneTreeDeleted'; payload: unknown }
  | { type: 'EscalationPolicyUpdated'; payload: unknown }
  | { type: 'EscalationEventUpdated'; payload: unknown }
  | { type: 'PhoneTreeEventCreated'; payload: unknown }
  | { type: 'PhoneTreeEventUpdated'; payload: unknown }
  | { type: 'IncidentCreated'; payload: unknown }
  | { type: 'IncidentUpdated'; payload: unknown }
  | { type: 'IncidentResolved'; payload: unknown }
  | { type: 'DispatchStepCompleted'; payload: unknown }

type EventHandler = (event: NotificationEvent) => void

let handlers: EventHandler[] = []

export function subscribe(handler: EventHandler) {
  handlers.push(handler)
  return () => {
    handlers = handlers.filter((h) => h !== handler)
  }
}

function notify(event: NotificationEvent) {
  handlers.forEach((h) => h(event))
}

/**
 * Gets or creates a SignalR connection to the backend notification hub.
 * Automatically reconnects on disconnect.
 */
export async function connectSignalR(accessToken: string): Promise<HubConnection> {
  if (connection && connection.state === HubConnectionState.Connected) {
    return connection
  }

  // Clean up any old connection
  if (connection) {
    await connection.stop().catch(() => {})
  }

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/notifications', {
      accessTokenFactory: () => accessToken,
    })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(LogLevel.Information)
    .build()

  // Register event handlers
  connection.on('ScheduleCreated', (payload: unknown) => {
    notify({ type: 'ScheduleCreated', payload })
  })

  connection.on('ShiftAssigned', (payload: unknown) => {
    notify({ type: 'ShiftAssigned', payload })
  })

  connection.on('ShiftsGenerated', (payload: unknown) => {
    notify({ type: 'ShiftsGenerated', payload })
  })

  connection.on('SwapRequested', (payload: unknown) => {
    notify({ type: 'SwapRequested', payload })
  })

  connection.on('SwapApproved', (payload: unknown) => {
    notify({ type: 'SwapApproved', payload })
  })

  connection.on('TimeOffUpdated', (payload: unknown) => {
    notify({ type: 'TimeOffUpdated', payload })
  })

  connection.on('ScheduleUpdated', (payload: unknown) => {
    notify({ type: 'ScheduleUpdated', payload })
  })

  connection.on('ScheduleDeleted', (payload: unknown) => {
    notify({ type: 'ScheduleDeleted', payload })
  })

  connection.on('EmployeeCreated', (payload: unknown) => {
    notify({ type: 'EmployeeCreated', payload })
  })

  connection.on('EmployeeUpdated', (payload: unknown) => {
    notify({ type: 'EmployeeUpdated', payload })
  })

  connection.on('EmployeeDeactivated', (payload: unknown) => {
    notify({ type: 'EmployeeDeactivated', payload })
  })

  connection.on('DepartmentCreated', (payload: unknown) => {
    notify({ type: 'DepartmentCreated', payload })
  })

  connection.on('DepartmentUpdated', (payload: unknown) => {
    notify({ type: 'DepartmentUpdated', payload })
  })

  connection.on('DepartmentDeactivated', (payload: unknown) => {
    notify({ type: 'DepartmentDeactivated', payload })
  })

  connection.on('PhoneTreeCreated', (payload: unknown) => {
    notify({ type: 'PhoneTreeCreated', payload })
  })

  connection.on('PhoneTreeUpdated', (payload: unknown) => {
    notify({ type: 'PhoneTreeUpdated', payload })
  })

  connection.on('PhoneTreeDeleted', (payload: unknown) => {
    notify({ type: 'PhoneTreeDeleted', payload })
  })

  connection.on('EscalationPolicyUpdated', (payload: unknown) => {
    notify({ type: 'EscalationPolicyUpdated', payload })
  })

  connection.on('EscalationEventUpdated', (payload: unknown) => {
    notify({ type: 'EscalationEventUpdated', payload })
  })

  connection.on('PhoneTreeEventCreated', (payload: unknown) => {
    notify({ type: 'PhoneTreeEventCreated', payload })
  })

  connection.on('PhoneTreeEventUpdated', (payload: unknown) => {
    notify({ type: 'PhoneTreeEventUpdated', payload })
  })

  connection.on('IncidentCreated', (payload: unknown) => {
    notify({ type: 'IncidentCreated', payload })
  })

  connection.on('IncidentUpdated', (payload: unknown) => {
    notify({ type: 'IncidentUpdated', payload })
  })

  connection.on('IncidentResolved', (payload: unknown) => {
    notify({ type: 'IncidentResolved', payload })
  })

  connection.on('DispatchStepCompleted', (payload: unknown) => {
    notify({ type: 'DispatchStepCompleted', payload })
  })

  connection.onreconnecting((error) => {
    console.warn('[SignalR] Reconnecting...', error)
  })

  connection.onreconnected((_connectionId) => {
    // Reconnected
  })

  connection.onclose((error) => {
    console.warn('[SignalR] Disconnected', error)
  })

  await connection.start()
  return connection
}

/**
 * Disconnect from the SignalR hub.
 */
export async function disconnectSignalR(): Promise<void> {
  if (connection) {
    await connection.stop().catch(() => {})
    connection = null
    handlers = []
  }
}
