import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { Alert, Severity, StatusCounts } from '../api/types';
import { severityRank } from '../api/types';
import { enableSound, playSeverity, soundReady } from './sound';

export interface Toast {
  key: string;
  alert: Alert;
  kind: 'raised' | 'resolved';
  at: number;
}

export interface Summary {
  devices: StatusCounts;
  alerts: Record<Severity, number>;
  serverTimeUtc: string;
}

type Listener = (payload: unknown) => void;

interface LiveState {
  connected: boolean;
  summary: Summary | null;
  toasts: Toast[];
  dismissToast: (key: string) => void;
  soundOn: boolean;
  toggleSound: () => Promise<void>;
  subscribe: (event: string, listener: Listener) => () => void;
  joinDevice: (deviceId: number) => () => void;
}

const LiveContext = createContext<LiveState | null>(null);

const SOUND_KEY = 'netwatch.sound';
const MIN_SOUND_SEVERITY: Severity = 'Warning';

/**
 * Single SignalR connection per tab. Turns server events into query-cache invalidations, alert pop-ups and
 * sounds, and lets pages subscribe to raw events (e.g. per-device metrics).
 */
export function LiveProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [connected, setConnected] = useState(false);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [soundOn, setSoundOn] = useState(false);
  const listeners = useRef(new Map<string, Set<Listener>>());
  const connectionRef = useRef<HubConnection | null>(null);
  const deviceRefs = useRef(new Map<number, number>());
  const invalidateTimer = useRef<number | null>(null);

  const emit = useCallback((event: string, payload: unknown) => {
    listeners.current.get(event)?.forEach((l) => l(payload));
  }, []);

  // Coalesce cache invalidations: with hundreds of devices many events arrive per second.
  const scheduleInvalidate = useCallback(() => {
    if (invalidateTimer.current !== null) return;
    invalidateTimer.current = window.setTimeout(() => {
      invalidateTimer.current = null;
      void queryClient.invalidateQueries({ queryKey: ['alerts'] });
      void queryClient.invalidateQueries({ queryKey: ['overview'] });
      void queryClient.invalidateQueries({ queryKey: ['devices'] });
    }, 1500);
  }, [queryClient]);

  const pushToast = useCallback((alert: Alert, kind: Toast['kind']) => {
    const toast: Toast = { key: `${alert.id}-${kind}-${Date.now()}`, alert, kind, at: Date.now() };
    setToasts((list) => [toast, ...list].slice(0, 6));
    const ttl = kind === 'resolved' ? 8000 : alert.severity === 'Critical' ? 60000 : 20000;
    window.setTimeout(() => setToasts((list) => list.filter((t) => t.key !== toast.key)), ttl);
  }, []);

  useEffect(() => {
    try {
      if (localStorage.getItem(SOUND_KEY) === 'on') {
        // Audio can only start after a click; the first click anywhere re-enables it.
        const resume = () => {
          void enableSound().then((ok) => setSoundOn(ok));
          window.removeEventListener('pointerdown', resume);
        };
        window.addEventListener('pointerdown', resume);
      }
    } catch {
      /* storage unavailable */
    }
  }, []);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30000, 1000 * 2 ** Math.min(ctx.previousRetryCount, 5)) })
      .configureLogging(LogLevel.Warning)
      .build();
    connectionRef.current = connection;

    connection.on('alertRaised', (alert: Alert) => {
      emit('alertRaised', alert);
      scheduleInvalidate();
      pushToast(alert, 'raised');
      if (severityRank[alert.severity] >= severityRank[MIN_SOUND_SEVERITY] && soundReady()) playSeverity(alert.severity);
    });
    connection.on('alertUpdated', (alert: Alert) => {
      emit('alertUpdated', alert);
      scheduleInvalidate();
    });
    connection.on('alertResolved', (alert: Alert) => {
      emit('alertResolved', alert);
      scheduleInvalidate();
      pushToast(alert, 'resolved');
    });
    connection.on('deviceStatus', (payload: unknown) => {
      emit('deviceStatus', payload);
      scheduleInvalidate();
    });
    connection.on('deviceMetrics', (payload: unknown) => emit('deviceMetrics', payload));
    connection.on('summary', (payload: Summary) => {
      setSummary(payload);
      emit('summary', payload);
    });

    connection.onreconnecting(() => setConnected(false));
    connection.onreconnected(() => {
      setConnected(true);
      for (const id of deviceRefs.current.keys()) void connection.invoke('JoinDevice', id).catch(() => {});
      scheduleInvalidate();
    });
    connection.onclose(() => setConnected(false));

    let stopped = false;
    const start = async () => {
      while (!stopped && connection.state === HubConnectionState.Disconnected) {
        try {
          await connection.start();
          setConnected(true);
          for (const id of deviceRefs.current.keys()) void connection.invoke('JoinDevice', id).catch(() => {});
        } catch {
          await new Promise((r) => setTimeout(r, 5000));
        }
      }
    };
    void start();

    return () => {
      stopped = true;
      void connection.stop();
      connectionRef.current = null;
    };
  }, [emit, pushToast, scheduleInvalidate]);

  const subscribe = useCallback((event: string, listener: Listener) => {
    let set = listeners.current.get(event);
    if (!set) {
      set = new Set();
      listeners.current.set(event, set);
    }
    set.add(listener);
    return () => {
      set!.delete(listener);
    };
  }, []);

  const joinDevice = useCallback((deviceId: number) => {
    const refs = deviceRefs.current;
    refs.set(deviceId, (refs.get(deviceId) ?? 0) + 1);
    const conn = connectionRef.current;
    if (conn?.state === HubConnectionState.Connected && refs.get(deviceId) === 1) {
      void conn.invoke('JoinDevice', deviceId).catch(() => {});
    }
    return () => {
      const n = (refs.get(deviceId) ?? 1) - 1;
      if (n <= 0) {
        refs.delete(deviceId);
        const c = connectionRef.current;
        if (c?.state === HubConnectionState.Connected) void c.invoke('LeaveDevice', deviceId).catch(() => {});
      } else {
        refs.set(deviceId, n);
      }
    };
  }, []);

  const toggleSound = useCallback(async () => {
    if (soundOn) {
      setSoundOn(false);
      try {
        localStorage.setItem(SOUND_KEY, 'off');
      } catch {
        /* ignore */
      }
      return;
    }
    const ok = await enableSound();
    setSoundOn(ok);
    if (ok) playSeverity('Info');
    try {
      localStorage.setItem(SOUND_KEY, ok ? 'on' : 'off');
    } catch {
      /* ignore */
    }
  }, [soundOn]);

  const dismissToast = useCallback((key: string) => setToasts((list) => list.filter((t) => t.key !== key)), []);

  const value = useMemo<LiveState>(
    () => ({ connected, summary, toasts, dismissToast, soundOn, toggleSound, subscribe, joinDevice }),
    [connected, summary, toasts, dismissToast, soundOn, toggleSound, subscribe, joinDevice],
  );

  return <LiveContext.Provider value={value}>{children}</LiveContext.Provider>;
}

export function useLive(): LiveState {
  const ctx = useContext(LiveContext);
  if (!ctx) throw new Error('useLive outside LiveProvider');
  return ctx;
}

/** Subscribes to a live event for the lifetime of the component. */
export function useLiveEvent<T>(event: string, handler: (payload: T) => void) {
  const { subscribe } = useLive();
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => subscribe(event, (p) => ref.current(p as T)), [event, subscribe]);
}
