import { cn } from '@/lib/utils';

interface AvatarProps {
  firstName: string;
  lastName: string;
  color?: string;
  size?: 'sm' | 'md' | 'lg';
  status?: 'online' | 'away' | 'offline';
}

const sizeClasses = { sm: 'h-7 w-7 text-xs', md: 'h-9 w-9 text-sm', lg: 'h-11 w-11 text-base' };
const statusColors = { online: 'bg-green-500', away: 'bg-amber-500', offline: 'bg-muted-foreground/40' };

export function Avatar({ firstName, lastName, color, size = 'md', status }: AvatarProps) {
  const initials = `${firstName[0] ?? ''}${lastName[0] ?? ''}`.toUpperCase();

  return (
    <div className="relative inline-flex">
      <div
        className={cn('flex items-center justify-center rounded-full font-semibold text-white', sizeClasses[size])}
        style={{ backgroundColor: color ?? 'hsl(var(--primary))' }}
      >
        {initials}
      </div>
      {status && (
        <span
          className={cn(
            'absolute bottom-0 right-0 h-2.5 w-2.5 rounded-full border-2 border-card',
            statusColors[status],
          )}
        />
      )}
    </div>
  );
}
