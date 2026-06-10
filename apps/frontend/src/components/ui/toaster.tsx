'use client';

import { X } from 'lucide-react';
import { cn } from '@/lib/utils';
import { useToastStore } from '@/hooks/use-toast';

export function Toaster() {
  const { toasts, dismiss } = useToastStore();

  if (!toasts.length) return null;

  return (
    <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2 w-full max-w-sm">
      {toasts.map((t) => (
        <div
          key={t.id}
          className={cn(
            'rounded-lg border bg-card p-4 shadow-lg animate-in slide-in-from-bottom-2',
            t.variant === 'success' && 'border-green-500/30',
            t.variant === 'destructive' && 'border-destructive/30',
          )}
        >
          <div className="flex items-start justify-between gap-2">
            <div>
              <p className="text-sm font-medium">{t.title}</p>
              {t.description && (
                <p className="text-xs text-muted-foreground mt-1">{t.description}</p>
              )}
            </div>
            <button
              type="button"
              onClick={() => dismiss(t.id)}
              className="text-muted-foreground hover:text-foreground"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}
