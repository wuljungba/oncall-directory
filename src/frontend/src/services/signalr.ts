import {
  HubConnectionBuilder,
  type HubConnection,
  LogLevel,
  HubConnectionState,
} from '@microsoft/signalr'

let connection: HubConnection | null = null

export type NotificationEvent =
  | { type: 'ScheduleCreated'; payload: unknown }
  | { type: 'ShiftAssigned'; payload: unknown }
  | { type: 'ShiftsGenerated'; payload: unknown }
  | { type: 'SwapRequested'; payload: unknown }
  | { type: 'SwapApproved'; payload: unknown }
  | { type: 'TimeOffUpdated'; payload: unknown }

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
