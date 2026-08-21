import type { FleetBridge } from '@shared/contract'

// The preload script puts exactly one thing on the global object. Declaring it here is what
// makes the renderer's access to it type-checked rather than an `any` reaching into nowhere.
declare global {
  interface Window {
    fleet: FleetBridge
  }
}

export {}
