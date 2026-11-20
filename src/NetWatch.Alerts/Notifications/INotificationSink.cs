namespace NetWatch.Alerts.Notifications;

public enum NotificationKind
{
    Raised,
    Resolved,
    Escalated,
    Test
}

/// <summary>An alert lifecycle change to deliver over the configured offline channels.</summary>
public sealed record AlertNotification(NotificationKind Kind, AlertDto Alert);

/// <summary>Queues notifications for delivery. Never blocks the alert engine.</summary>
public interface INotificationSink
{
    void Enqueue(AlertNotification notification);
}
