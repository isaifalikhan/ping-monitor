import { useSearchParams } from 'react-router-dom';
import { AuditTab } from './AuditTab';
import { CredentialsTab } from './CredentialsTab';
import { DirectoryTab } from './DirectoryTab';
import { GeneralTab } from './GeneralTab';
import { GroupsTab } from './GroupsTab';
import { MaintenanceTab } from './MaintenanceTab';
import { NotificationsTab } from './NotificationsTab';
import { RulesTab } from './RulesTab';
import { SystemTab } from './SystemTab';
import { UsersTab } from './UsersTab';
import { PilotToolsTab } from './PilotToolsTab';

const TABS = [
  { key: 'rules', label: 'Alert rules', el: <RulesTab /> },
  { key: 'maintenance', label: 'Maintenance windows', el: <MaintenanceTab /> },
  { key: 'notifications', label: 'Notifications', el: <NotificationsTab /> },
  { key: 'credentials', label: 'Credentials', el: <CredentialsTab /> },
  { key: 'groups', label: 'Groups & sites', el: <GroupsTab /> },
  { key: 'users', label: 'Users', el: <UsersTab /> },
  { key: 'directory', label: 'Directory (AD)', el: <DirectoryTab /> },
  { key: 'general', label: 'General', el: <GeneralTab /> },
  { key: 'audit', label: 'Audit trail', el: <AuditTab /> },
  { key: 'system', label: 'System status', el: <SystemTab /> },
  { key: 'pilot', label: 'Pilot tools', el: <PilotToolsTab /> },
];

export function SettingsPage() {
  const [params, setParams] = useSearchParams();
  const active = TABS.find((t) => t.key === params.get('tab')) ?? TABS[0];
  return (
    <>
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <div className="tabs">
        {TABS.map((t) => (
          <button key={t.key} className={t.key === active.key ? 'active' : ''} onClick={() => setParams({ tab: t.key }, { replace: true })}>
            {t.label}
          </button>
        ))}
      </div>
      {active.el}
    </>
  );
}
