// Alert sounds synthesised with the Web Audio API: no audio files, nothing to download.
// Browsers only allow audio after a user gesture, so sound must be enabled once per tab (button in the top bar).
import type { Severity } from '../api/types';

let ctx: AudioContext | null = null;

const patterns: Record<Severity, { freq: number; dur: number; gap: number; count: number; type: OscillatorType }> = {
  Info: { freq: 660, dur: 0.12, gap: 0.1, count: 1, type: 'sine' },
  Warning: { freq: 740, dur: 0.18, gap: 0.12, count: 2, type: 'sine' },
  Major: { freq: 880, dur: 0.2, gap: 0.1, count: 3, type: 'triangle' },
  Critical: { freq: 990, dur: 0.22, gap: 0.08, count: 5, type: 'square' },
};

export function soundAvailable(): boolean {
  return typeof window !== 'undefined' && ('AudioContext' in window || 'webkitAudioContext' in window);
}

/** Must be called from a click handler. Returns true if audio is now usable. */
export async function enableSound(): Promise<boolean> {
  if (!soundAvailable()) return false;
  if (!ctx) {
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    ctx = new Ctor();
  }
  if (ctx.state === 'suspended') await ctx.resume();
  return ctx.state === 'running';
}

export function soundReady(): boolean {
  return !!ctx && ctx.state === 'running';
}

export function playSeverity(severity: Severity, volume = 0.25) {
  if (!ctx || ctx.state !== 'running') return;
  const p = patterns[severity];
  let t = ctx.currentTime + 0.02;
  for (let i = 0; i < p.count; i++) {
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = p.type;
    // Critical alternates between two tones like a siren.
    osc.frequency.value = severity === 'Critical' && i % 2 === 1 ? p.freq * 0.75 : p.freq;
    gain.gain.setValueAtTime(0, t);
    gain.gain.linearRampToValueAtTime(volume, t + 0.01);
    gain.gain.setValueAtTime(volume, t + p.dur - 0.03);
    gain.gain.linearRampToValueAtTime(0, t + p.dur);
    osc.connect(gain).connect(ctx.destination);
    osc.start(t);
    osc.stop(t + p.dur + 0.02);
    t += p.dur + p.gap;
  }
}
