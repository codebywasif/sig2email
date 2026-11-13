import { Link as RouterLink } from 'react-router-dom';
import { Link, Text } from '@fluentui/react-components';
import { CheckmarkCircleFilled, CircleRegular } from '@fluentui/react-icons';
import { api, type TenantInfo } from '../api';
import { useAuth } from '../auth';
import { ColumnChart, StatTile } from '../charts';
import { LoadState, Page, fmtDate, fmtDay, useLoad } from '../common';

interface Overview {
  mail: { day: string; processed: number; stamped: number; failed: number }[];
  processed: number; stamped: number; responses: number; csat: number | null; clicks: number;
}

const MODE_LABEL = { ServerSide: 'Server-side (mail flow)', ClientSide: 'Client-side (Outlook add-in)', Combo: 'Combo (add-in + mail flow)' } as const;

export function Dashboard() {
  const { can } = useAuth();
  const tenant = useLoad(() => api.get<TenantInfo>('/api/tenant'));
  const overview = useLoad(() => api.get<Overview>('/api/analytics/overview'));
  const t = tenant.data;
  const o = overview.data;

  const steps = t && [
    { done: t.connected, label: 'Connect Microsoft 365', hint: 'Enter the Entra app registration under System so users and groups can be read.', to: '/system' },
    { done: t.counts.users > 0, label: 'Synchronise the directory', hint: t.lastSyncAt ? `Last sync ${fmtDate(t.lastSyncAt)}` : 'Runs automatically once Microsoft 365 is connected.', to: '/directory' },
    { done: t.counts.templates > 0, label: 'Design a signature template', hint: `${t.counts.templates} template(s)`, to: '/templates' },
    { done: t.counts.rules > 0, label: 'Create a rule that assigns it', hint: `${t.counts.rules} active rule(s)`, to: '/rules' },
    { done: t.mode === 'ClientSide' || !!t.relayHost, label: 'Connect mail flow or deploy the Outlook add-in', hint: MODE_LABEL[t.mode], to: '/settings' },
  ];

  return (
    <Page title="Overview" description={t ? `${t.name} · ${MODE_LABEL[t.mode]}` : undefined}>
      <LoadState loading={tenant.loading || overview.loading} error={tenant.error ?? overview.error} onRetry={() => { void tenant.reload(); void overview.reload(); }} />
      {t && o && (
        <>
          <div className="tiles">
            <StatTile label="Messages stamped · 30 days" value={o.stamped.toLocaleString()} note={`${o.processed.toLocaleString()} processed`} />
            <StatTile label="Directory users" value={t.counts.users.toLocaleString()} note={`${t.counts.groups} groups`} />
            <StatTile label="Active rules" value={String(t.counts.rules)} note={`${t.counts.templates} templates`} />
            <StatTile label="CSAT · 30 days" value={o.csat == null ? '—' : `${o.csat}%`} note={`${o.responses} responses`} />
            <StatTile label="Tracked link clicks · 30 days" value={o.clicks.toLocaleString()} />
          </div>
          <div className="grid-2">
            <section className="panel">
              <h2>Messages stamped per day</h2>
              {o.stamped === 0
                ? <Text className="muted">No mail has passed through the server-side pipeline yet.</Text>
                : <ColumnChart ariaLabel="Messages stamped per day, last 30 days" unit="messages" data={o.mail.map((m) => ({ label: fmtDay(m.day), value: m.stamped, note: `${m.processed.toLocaleString()} processed` }))} />}
            </section>
            <section className="panel">
              <h2>Setup checklist</h2>
              <ul className="checklist">
                {steps!.map((s) => (
                  <li key={s.label}>
                    {s.done ? <CheckmarkCircleFilled className="ok" aria-label="Done" /> : <CircleRegular className="todo" aria-label="To do" />}
                    <div>
                      {(s.to === '/system' || s.to === '/settings' ? can() : can('RuleAdmin', 'Designer')) ? <RouterLink to={s.to} style={{ color: 'inherit' }}><Link as="span">{s.label}</Link></RouterLink> : <Text>{s.label}</Text>}
                      <br /><Text size={200} className="muted">{s.hint}</Text>
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          </div>
        </>
      )}
    </Page>
  );
}
