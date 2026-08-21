import { join } from 'node:path'
import { app, BrowserWindow, ipcMain, shell } from 'electron'
import { IPC } from '@shared/contract'
import { FleetTransport } from './transport'

// Electron honours ELECTRON_RUN_AS_NODE by starting as a plain Node process with no browser
// and no `app` object, which turns every call below into an undebuggable TypeError. Some
// editor-integrated terminals export it, and child processes inherit it, so the failure
// arrives with no obvious cause. Saying so beats crashing on `app.whenReady`.
if (!app) {
  console.error(
    'ELECTRON_RUN_AS_NODE is set in this environment, so Electron started as Node. ' +
      'Unset it before launching the app.',
  )
  process.exit(1)
}

const API_URL = process.env.FLEET_API_URL ?? 'http://localhost:8080'
const MAX_RATE_HZ = Number(process.env.FLEET_MAX_RATE_HZ ?? 4)

// Held so the transport can be stopped when the last window goes. The windows themselves are
// reached through BrowserWindow, not tracked here.
let transport: FleetTransport | null = null

function createWindow(): BrowserWindow {
  const created = new BrowserWindow({
    width: 1360,
    height: 820,
    show: false,
    backgroundColor: '#14161a',
    title: 'Fleet',
    webPreferences: {
      // __dirname, not import.meta: main and preload are built as CommonJS. Electron's own
      // module has no named ESM exports, and a sandboxed preload cannot be an ES module at
      // all — the sandbox has no module loader in it.
      preload: join(__dirname, '../preload/index.js'),
      // The renderer is treated as untrusted, which is the default posture for anything
      // rendering data from a network service. It gets no Node integration and no shared
      // global scope with the preload script; everything it can do arrives through the
      // narrow bridge in src/preload.
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  })

  // Painting an empty window and then filling it produces a visible flash. Waiting for the
  // first paint costs nothing and starts the app on a rendered frame.
  created.once('ready-to-show', () => created.show())

  // Anything that tries to navigate the shell away from the app — a link in an event
  // message, say — opens in the user's browser instead. A renderer that can be navigated to
  // an arbitrary page is a renderer that can be phished.
  created.webContents.setWindowOpenHandler(({ url }) => {
    void shell.openExternal(url)
    return { action: 'deny' }
  })

  if (process.env.ELECTRON_RENDERER_URL) {
    void created.loadURL(process.env.ELECTRON_RENDERER_URL)
  } else {
    void created.loadFile(join(__dirname, '../renderer/index.html'))
  }

  return created
}

function startTransport(target: BrowserWindow): FleetTransport {
  const send = (channel: string, payload: unknown): void => {
    // The window can be gone while a frame is in flight during shutdown or a reload.
    if (!target.isDestroyed()) target.webContents.send(channel, payload)
  }

  const created = new FleetTransport(
    { apiUrl: API_URL, maxRateHz: MAX_RATE_HZ },
    (frame) => send(IPC.frame, frame),
    (status) => send(IPC.status, status),
  )
  created.start()
  return created
}

app.whenReady().then(() => {
  createWindow()

  // The renderer starts the session, not the window.
  //
  // Connecting here instead would race the renderer: the snapshot arrives within a
  // millisecond or two of the socket opening, long before a React tree has mounted and
  // subscribed, and a frame sent to nobody is discarded. Letting the renderer say when it is
  // listening also gives reloads the right behaviour — a reloaded window is a new session and
  // gets its own snapshot, rather than inheriting a stream mid-flight.
  ipcMain.handle(IPC.ready, (event) => {
    const target = BrowserWindow.fromWebContents(event.sender)
    if (!target) return
    transport?.stop()
    transport = startTransport(target)
  })

  ipcMain.handle(IPC.statusQuery, () => transport?.currentStatus() ?? null)
  ipcMain.handle(IPC.history, (_event, deviceId: string, minutes: number) =>
    transport?.history(deviceId, minutes) ?? [],
  )
  ipcMain.handle(IPC.events, (_event, deviceId: string | null, limit: number) =>
    transport?.events(deviceId, limit) ?? [],
  )

  // macOS convention: re-create a window when the dock icon is clicked with none open. The
  // dashboards are Windows-targeted, but this costs two lines and its absence is the kind of
  // thing that reads as carelessness on any other platform.
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  transport?.stop()
  transport = null
  if (process.platform !== 'darwin') app.quit()
})
