import { dispatchAlerts, type AlertDeliveryParams } from '@/lib/alerts/dispatcher';
import { evaluateCheckTriggers, type AlertTrigger } from '@/lib/alerts/triggers';

export async function dispatchCheckAlerts(params: {
  organizationId: string;
  monitorId: string;
  monitorName: string;
  monitorType: string;
  previousStatus: string;
  currentStatus: string;
  errorMessage?: string;
  pauseAlerts: boolean;
  message: string;
}) {
  if (params.pauseAlerts) return { sent: 0, failed: 0, triggers: [] as AlertTrigger[] };

  const triggers = evaluateCheckTriggers({
    monitorType: params.monitorType,
    previousStatus: params.previousStatus,
    currentStatus: params.currentStatus,
    errorMessage: params.errorMessage,
  });

  let sent = 0;
  let failed = 0;
  for (const trigger of triggers) {
    const result = await dispatchAlerts({
      organizationId: params.organizationId,
      trigger,
      monitorId: params.monitorId,
      monitorName: params.monitorName,
      message: params.message,
    });
    sent += result.sent;
    failed += result.failed;
  }

  return { sent, failed, triggers };
}
