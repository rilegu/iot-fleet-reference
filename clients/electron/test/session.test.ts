import { describe, expect, it, vi } from 'vitest'

/**
 * Regression test for a bug that shipped and was found by running the app against the live
 * API, not by reading the code.
 *
 * The main process used to open the socket as soon as the window was created. The API sends
 * its snapshot within a millisecond or two of accepting the connection — long before a React
 * tree has mounted — and a frame sent to a renderer with no listener is discarded silently.
 *
 * Nothing looked wrong. Deltas refilled the grid within a second, so the fleet appeared, the
 * counts moved, and the dashboard seemed healthy. What was actually missing was every device
 * that had *not* changed since connecting, plus the cadence, which only the snapshot carries:
 * against a live fleet the client showed 100 devices where the server had 112.
 *
 * The fix is ordering, so the test is about ordering: the frame listener must be registered
 * before the main process is told to connect, and both must happen when the module loads
 * rather than in an effect.
 */
describe('session startup', () => {
  it('subscribes to frames before asking the main process to connect', async () => {
    const calls: string[] = []

    vi.stubGlobal('window', {
      fleet: {
        onFrame: () => {
          calls.push('onFrame')
          return () => {}
        },
        ready: async () => {
          calls.push('ready')
        },
        onStatus: () => () => {},
        status: async () => null,
        history: async () => [],
        events: async () => [],
      },
    })

    // A fresh import, so module-scope side effects run inside this test.
    vi.resetModules()
    await import('../src/renderer/src/fleet/useFleet')

    expect(calls).toEqual(['onFrame', 'ready'])
  })
})
