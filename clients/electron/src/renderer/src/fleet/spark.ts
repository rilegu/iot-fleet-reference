import type { TelemetryPoint } from '@shared/contract'

export interface SparkPoint {
  x: number
  y: number
}

export interface Spark {
  points: SparkPoint[]
  min: number
  max: number
}

/**
 * Turns a bucket series into sparkline vertices normalised to a 0-100 box.
 *
 * The same arithmetic as `DeviceDetailViewModel.BuildSpark` in the XAML clients, and
 * normalised for the same reason: the shape is independent of the size it is drawn at. Here
 * that box maps straight onto an SVG `viewBox`, so the browser does the scaling and there is
 * no resize handler at all — the one place this stack is genuinely less work than XAML,
 * where each host has to project the vertices onto a canvas itself.
 */
export function buildSpark(history: readonly TelemetryPoint[]): Spark {
  const values = history
    .map((p) => p.temp_c_avg)
    .filter((v): v is number => typeof v === 'number')

  const min = values.length > 0 ? Math.min(...values) : 0
  const max = values.length > 0 ? Math.max(...values) : 0

  // A single point is not a line, and drawing one would be misleading.
  if (values.length < 2) return { points: [], min, max }

  // A flat series has zero range, which would divide by zero and make every vertex NaN — a
  // line that draws as nothing. Clamping the divisor keeps it a straight line at the floor of
  // the box, which is what a reading pinned to its own minimum is.
  const range = Math.max(max - min, 0.001)
  const stepX = 100 / (values.length - 1)

  const points = values.map((value, i) => ({
    x: i * stepX,
    // Y is inverted: SVG grows Y downward, so hot belongs at the top.
    y: 100 - ((value - min) / range) * 100,
  }))

  return { points, min, max }
}
