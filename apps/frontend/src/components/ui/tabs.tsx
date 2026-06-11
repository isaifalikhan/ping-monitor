'use client';

import { cn } from '@/lib/utils';

interface TabsProps {
  tabs: { id: string; label: string; count?: number }[];
  active: string;
  onChange: (id: string) => void;
  className?: string;
}

export function Tabs({ tabs, active, onChange, className }: TabsProps) {
  return (
    <div className={cn('flex gap-1 border-b', className)}>
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          onClick={() => onChange(tab.id)}
          className={cn(
            'relative px-4 py-2.5 text-sm font-medium transition-colors hover:text-foreground',
            active === tab.id
              ? 'text-foreground after:absolute after:bottom-0 after:left-0 after:right-0 after:h-0.5 after:bg-primary'
              : 'text-muted-foreground',
          )}
        >
          {tab.label}
          {tab.count != null && (
            <span className="ml-1.5 rounded-full bg-muted px-1.5 py-0.5 text-xs">{tab.count}</span>
          )}
        </button>
      ))}
    </div>
  );
}
