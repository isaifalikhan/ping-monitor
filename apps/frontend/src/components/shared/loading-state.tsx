import { Skeleton } from '@/components/ui/skeleton';

export function LoadingState({
  message = 'Loading...',
  variant = 'spinner',
}: {
  message?: string;
  variant?: 'spinner' | 'skeleton';
}) {
  if (variant === 'skeleton') {
    return (
      <div className="space-y-4 py-4">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-24 w-full" />
          ))}
        </div>
        <Skeleton className="h-64 w-full" />
        <Skeleton className="h-48 w-full" />
        <p className="text-sm text-muted-foreground text-center">{message}</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col items-center justify-center py-16 gap-3">
      <div className="h-8 w-8 animate-spin rounded-full border-4 border-primary border-t-transparent" />
      <p className="text-sm text-muted-foreground">{message}</p>
    </div>
  );
}
