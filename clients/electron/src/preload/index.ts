import { contextBridge, ipcRenderer } from 'electron'
import type {
  ConnectionStatus,
  DeviceEvent,
  FleetBridge,
  ServerFrame,
  TelemetryPoint,
} from '@shared/contract'
import { IPC } from '@shared/contract'

/**
 * The bridge between the sandboxed renderer and the main process.
 *
 * Everything the UI can do to the outside world is on this object, and nothing else crosses.
 * Note that the handlers are wrapped rather than passed straight to `ipcRenderer.on`: doing
 * that would hand the renderer the `IpcRendererEvent`, and with it `sender`, which is a route
 * back out of the sandbox.
 */
const bridge: FleetBridge = {
  onFrame(handler: (frame: ServerFrame) => void) {
    const listener = (_event: unknown, frame: ServerFrame): void => handler(frame)
    ipcRenderer.on(IPC.frame, listener)
    return () => ipcRenderer.removeListener(IPC.frame, listener)
  },

  onStatus(handler: (status: ConnectionStatus) => void) {
    const listener = (_event: unknown, status: ConnectionStatus): void => handler(status)
    ipcRenderer.on(IPC.status, listener)
    return () => ipcRenderer.removeListener(IPC.status, listener)
  },

  ready: () => ipcRenderer.invoke(IPC.ready) as Promise<void>,

  status: () => ipcRenderer.invoke(IPC.statusQuery) as Promise<ConnectionStatus>,

  history: (deviceId: string, minutes: number) =>
    ipcRenderer.invoke(IPC.history, deviceId, minutes) as Promise<TelemetryPoint[]>,

  events: (deviceId: string | null, limit: number) =>
    ipcRenderer.invoke(IPC.events, deviceId, limit) as Promise<DeviceEvent[]>,
}

contextBridge.exposeInMainWorld('fleet', bridge)
