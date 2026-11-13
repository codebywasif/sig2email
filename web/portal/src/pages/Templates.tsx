import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Table, TableBody, TableCell, TableHeader,
  TableHeaderCell, TableRow, Text,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { api, type TemplateSummary } from '../api';
import { useAuth } from '../auth';
import { ConfirmDialog, Empty, LoadState, Page, fmtDate, useAction, useLoad } from '../common';
import { STARTERS } from '../designer/blocks';

export function Templates() {
  const { can } = useAuth();
  const canEdit = can('Designer');
  const navigate = useNavigate();
  const templates = useLoad(() => api.get<TemplateSummary[]>('/api/templates'));
  const [picking, setPicking] = useState(false);
  const [deleting, setDeleting] = useState<TemplateSummary | null>(null);
  const { run } = useAction();

  return (
    <Page title="Templates" description="Signature, disclaimer and banner designs. Drafts only reach live mail once they are published."
      actions={canEdit && <Button appearance="primary" icon={<AddRegular />} onClick={() => setPicking(true)}>New template</Button>}>
      <LoadState loading={templates.loading && !templates.data} error={templates.error} onRetry={templates.reload} />
      {templates.data?.length === 0 && <section className="panel"><Empty title="No templates yet">{canEdit ? 'Start from a layout and personalise it with placeholders.' : 'A template designer has not created any templates yet.'}</Empty></section>}
      {templates.data && templates.data.length > 0 && (
        <section className="panel flush">
          <div className="table-wrap">
            <Table aria-label="Templates">
              <TableHeader><TableRow>
                <TableHeaderCell>Template</TableHeaderCell><TableHeaderCell>Status</TableHeaderCell><TableHeaderCell>Used by</TableHeaderCell>
                <TableHeaderCell>Last edited</TableHeaderCell>{canEdit && <TableHeaderCell style={{ width: 56 }}>Delete</TableHeaderCell>}
              </TableRow></TableHeader>
              <TableBody>
                {templates.data.map((t) => (
                  <TableRow key={t.id}>
                    <TableCell>
                      <Button appearance="transparent" className="link-button" onClick={() => navigate(`/templates/${t.id}`)}>{t.name}</Button>
                      <br /><Text size={200} className="muted">{t.description || (t.sourceMode ? 'HTML source' : 'Visual design')}</Text>
                    </TableCell>
                    <TableCell>
                      {t.publishedVersion == null
                        ? <Badge appearance="tint" color="warning">Draft — never published</Badge>
                        : t.hasUnpublishedChanges
                          ? <Badge appearance="tint" color="warning">Live v{t.publishedVersion} · unpublished changes</Badge>
                          : <Badge appearance="tint" color="success">Live v{t.publishedVersion}</Badge>}
                    </TableCell>
                    <TableCell>{t.ruleCount === 0 ? <span className="muted">No rules</span> : `${t.ruleCount} rule${t.ruleCount === 1 ? '' : 's'}`}</TableCell>
                    <TableCell>{fmtDate(t.updatedAt)}<br /><Text size={200} className="muted">{t.updatedBy}</Text></TableCell>
                    {canEdit && <TableCell><Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${t.name}`} onClick={() => setDeleting(t)} /></TableCell>}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </section>
      )}

      <Dialog open={picking} onOpenChange={(_, d) => setPicking(d.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Start from</DialogTitle>
            <DialogContent>
              <div className="stack tight">
                {STARTERS.map((s) => (
                  <Button key={s.key} style={{ justifyContent: 'flex-start', textAlign: 'left', height: 'auto', padding: '8px 12px' }} onClick={() => navigate(`/templates/new?starter=${s.key}`)}>
                    <span><strong>{s.name}</strong><br /><Text size={200} className="muted">{s.description}</Text></span>
                  </Button>
                ))}
              </div>
            </DialogContent>
            <DialogActions><Button onClick={() => setPicking(false)}>Cancel</Button></DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      <ConfirmDialog open={deleting != null} title="Delete template?" confirmLabel="Delete template" onClose={() => setDeleting(null)}
        body={<>“{deleting?.name}” and its version history will be deleted. Templates that rules still use cannot be deleted.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/templates/${deleting!.id}`); await templates.reload(); }, 'Template deleted')} />
    </Page>
  );
}
