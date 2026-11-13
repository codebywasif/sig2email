import { useState } from 'react';
import { Badge, Button, Checkbox, Dropdown, Option, Tab, TabList, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from '@fluentui/react-components';
import { api, type Survey } from '../api';
import { BarList, ColumnChart, StatTile } from '../charts';
import { Empty, LoadState, Page, fmtDate, fmtDay, useLoad } from '../common';

interface Csat {
  days: number;
  summary: { dispatched: number; responses: number; responseRate: number | null; average: number | null; maxScore: number; csat: number | null; negative: number };
  distribution: { score: number; count: number }[];
  trend: { day: string; responses: number; csat: number | null }[];
  bySender: { senderName: string; senderEmail: string; responses: number; average: number; csat: number | null }[];
}
interface Response { id: string; score: number; maxScore: number; comment: string | null; senderName: string; recipients: string; subject: string; createdAt: string }
interface Clicks { total: number; byDay: { day: string; clicks: number }[]; byUrl: { url: string; clicks: number }[]; byCampaign: { campaign: string; clicks: number }[] }

const RANGES = [{ days: 7, label: 'Last 7 days' }, { days: 30, label: 'Last 30 days' }, { days: 90, label: 'Last 90 days' }];

export function Analytics() {
  const [tab, setTab] = useState<'csat' | 'clicks'>('csat');
  const [days, setDays] = useState(30);
  const [surveyId, setSurveyId] = useState('');
  const [negativeOnly, setNegativeOnly] = useState(false);
  const [page, setPage] = useState(0);
  const surveys = useLoad(() => api.get<Survey[]>('/api/surveys'));
  const q = surveyId ? `&surveyId=${surveyId}` : '';
  const csat = useLoad(() => api.get<Csat>(`/api/analytics/csat?days=${days}${q}`), [days, surveyId]);
  const responses = useLoad(() => api.get<{ total: number; items: Response[] }>(`/api/analytics/responses?take=10&skip=${page * 10}&negativeOnly=${negativeOnly}${q}`), [surveyId, negativeOnly, page]);
  const clicks = useLoad(() => api.get<Clicks>(`/api/analytics/clicks?days=${days}`), [days]);
  const c = csat.data;

  return (
    <Page title="Analytics" description="Customer satisfaction from one-click surveys and engagement with links in signatures.">
      {/* One filter row above everything it scopes. */}
      <div className="row">
        <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'csat' | 'clicks')}>
          <Tab value="csat">Customer satisfaction</Tab>
          <Tab value="clicks">Link clicks</Tab>
        </TabList>
        <Dropdown className="right" aria-label="Date range" value={RANGES.find((r) => r.days === days)!.label} selectedOptions={[String(days)]} onOptionSelect={(_, d) => setDays(Number(d.optionValue))}>
          {RANGES.map((r) => <Option key={r.days} value={String(r.days)}>{r.label}</Option>)}
        </Dropdown>
        {tab === 'csat' && (
          <Dropdown aria-label="Survey" value={surveyId ? surveys.data?.find((s) => s.id === surveyId)?.name ?? '' : 'All surveys'} selectedOptions={[surveyId]}
            onOptionSelect={(_, d) => { setSurveyId(d.optionValue ?? ''); setPage(0); }}>
            <Option value="">All surveys</Option>
            {(surveys.data ?? []).map((s) => <Option key={s.id} value={s.id}>{s.name}</Option>)}
          </Dropdown>
        )}
      </div>

      {tab === 'csat' && (
        <>
          <LoadState loading={csat.loading && !c} error={csat.error} onRetry={csat.reload} />
          {c && (
            <>
              <div className="tiles">
                <StatTile label="CSAT" value={c.summary.csat == null ? '—' : `${c.summary.csat}%`} note="Share of ratings in the top 20% of the scale" />
                <StatTile label="Average rating" value={c.summary.average == null ? '—' : `${c.summary.average} / ${c.summary.maxScore}`} />
                <StatTile label="Responses" value={c.summary.responses.toLocaleString()} note={`${c.summary.dispatched.toLocaleString()} surveys sent`} />
                <StatTile label="Response rate" value={c.summary.responseRate == null ? '—' : `${c.summary.responseRate}%`} />
                <StatTile label="Negative ratings" value={String(c.summary.negative)} />
              </div>
              {c.summary.responses === 0 ? (
                <section className="panel"><Empty title="No ratings in this period">Ratings appear here as soon as recipients click a survey in a signature.</Empty></section>
              ) : (
                <div className="grid-2">
                  <section className="panel">
                    <h2>Responses per day</h2>
                    <ColumnChart ariaLabel="Survey responses per day" unit="responses"
                      data={c.trend.map((t) => ({ label: fmtDay(t.day), value: t.responses, note: t.csat == null ? undefined : `CSAT ${t.csat}%` }))} />
                  </section>
                  <section className="panel">
                    <h2>Rating distribution</h2>
                    <BarList data={[...c.distribution].reverse().map((d) => ({
                      label: `${d.score} of ${c.summary.maxScore}`, value: d.count,
                      note: `${Math.round((100 * d.count) / c.summary.responses)}%`,
                    }))} />
                  </section>
                  <section className="panel flush" style={{ gridColumn: '1 / -1' }}>
                    <h2 style={{ padding: '14px 16px 0', margin: 0, fontSize: 15 }}>By sender</h2>
                    <div className="table-wrap">
                      <Table size="small" aria-label="Satisfaction by sender">
                        <TableHeader><TableRow><TableHeaderCell>Sender</TableHeaderCell><TableHeaderCell>Responses</TableHeaderCell><TableHeaderCell>Average</TableHeaderCell><TableHeaderCell>CSAT</TableHeaderCell></TableRow></TableHeader>
                        <TableBody>
                          {c.bySender.map((s) => (
                            <TableRow key={s.senderEmail}>
                              <TableCell>{s.senderName}<br /><Text size={200} className="muted">{s.senderEmail}</Text></TableCell>
                              <TableCell>{s.responses}</TableCell><TableCell>{s.average}</TableCell><TableCell>{s.csat == null ? '—' : `${s.csat}%`}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    </div>
                  </section>
                </div>
              )}
              <section className="panel flush">
                <div className="row" style={{ padding: '12px 16px 4px' }}>
                  <h2 style={{ margin: 0, fontSize: 15 }}>Individual responses</h2>
                  <Checkbox className="right" label="Negative only" checked={negativeOnly} onChange={(_, d) => { setNegativeOnly(!!d.checked); setPage(0); }} />
                </div>
                <LoadState loading={responses.loading && !responses.data} error={responses.error} onRetry={responses.reload} />
                {responses.data?.items.length === 0 && <Empty title="No responses" />}
                {responses.data && responses.data.items.length > 0 && (
                  <div className="table-wrap">
                    <Table size="small" aria-label="Individual responses">
                      <TableHeader><TableRow>
                        <TableHeaderCell style={{ width: 150 }}>When</TableHeaderCell><TableHeaderCell style={{ width: 80 }}>Rating</TableHeaderCell><TableHeaderCell>Sender</TableHeaderCell>
                        <TableHeaderCell>Recipient</TableHeaderCell><TableHeaderCell>Subject</TableHeaderCell><TableHeaderCell>Comment</TableHeaderCell>
                      </TableRow></TableHeader>
                      <TableBody>
                        {responses.data.items.map((r) => (
                          <TableRow key={r.id}>
                            <TableCell className="nowrap">{fmtDate(r.createdAt)}</TableCell>
                            <TableCell><Badge appearance="tint" color={r.score * 5 <= r.maxScore * 2 ? 'danger' : r.score * 5 >= r.maxScore * 4 ? 'success' : 'informative'}>{r.score} / {r.maxScore}</Badge></TableCell>
                            <TableCell>{r.senderName}</TableCell><TableCell>{r.recipients}</TableCell><TableCell>{r.subject}</TableCell>
                            <TableCell>{r.comment ?? <span className="muted">—</span>}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                )}
                {responses.data && responses.data.total > 10 && (
                  <div className="row" style={{ padding: 10 }}>
                    <Text size={200} className="muted">Showing {page * 10 + 1}–{Math.min((page + 1) * 10, responses.data.total)} of {responses.data.total}</Text>
                    <Button size="small" className="right" disabled={page === 0} onClick={() => setPage(page - 1)}>Newer</Button>
                    <Button size="small" disabled={(page + 1) * 10 >= responses.data.total} onClick={() => setPage(page + 1)}>Older</Button>
                  </div>
                )}
              </section>
            </>
          )}
        </>
      )}

      {tab === 'clicks' && (
        <>
          <LoadState loading={clicks.loading && !clicks.data} error={clicks.error} onRetry={clicks.reload} />
          {clicks.data && (clicks.data.total === 0 ? (
            <section className="panel"><Empty title="No link clicks in this period">Turn on “Count clicks” in a rule to measure engagement with signature links and banners.</Empty></section>
          ) : (
            <>
              <div className="tiles"><StatTile label="Link clicks" value={clicks.data.total.toLocaleString()} note={RANGES.find((r) => r.days === days)!.label} /></div>
              <div className="grid-2">
                <section className="panel" style={{ gridColumn: '1 / -1' }}>
                  <h2>Clicks per day</h2>
                  <ColumnChart ariaLabel="Link clicks per day" unit="clicks" data={clicks.data.byDay.map((d) => ({ label: fmtDay(d.day), value: d.clicks }))} />
                </section>
                <section className="panel"><h2>By destination</h2><BarList data={clicks.data.byUrl.map((u) => ({ label: u.url.replace(/^https?:\/\//, ''), value: u.clicks }))} /></section>
                <section className="panel"><h2>By campaign (utm_campaign)</h2><BarList data={clicks.data.byCampaign.map((u) => ({ label: u.campaign, value: u.clicks }))} /></section>
              </div>
            </>
          ))}
        </>
      )}
    </Page>
  );
}
