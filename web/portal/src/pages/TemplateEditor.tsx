import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuList,
  MenuPopover, MenuTrigger, MessageBar, MessageBarBody, Radio, RadioGroup, Spinner, Tab, TabList, Text, ToggleButton,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DeleteRegular, DesktopRegular, HistoryRegular, PhoneRegular, WeatherMoonRegular } from '@fluentui/react-icons';
import { api, type Asset, type Survey, type Template } from '../api';
import { useAuth } from '../auth';
import { ConfirmDialog, LoadState, Page, UserSelect, download, fmtDate, useAction, useLoad } from '../common';
import { BlockEditor } from '../designer/BlockEditor';
import { STARTERS, compile, type Design } from '../designer/blocks';

const SourceEditor = lazy(() => import('../designer/SourceEditor'));

interface Draft { name: string; description: string; html: string; design: Design | null; imageMode: 'Hosted' | 'Inline' }
interface Rendered { html: string; text: string; rtf: string; userId: string }
interface Version { version: number; publishedBy: string; publishedAt: string }

export default function TemplateEditor() {
  const { id } = useParams();
  const isNew = id === 'new';
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { can, config } = useAuth();
  const canEdit = can('Designer');
  const canPublish = can('RuleAdmin');
  const { run, busy } = useAction();
  const dark = document.documentElement.dataset.theme === 'dark';

  const existing = useLoad(() => (isNew ? Promise.resolve(null) : api.get<Template>(`/api/templates/${id}`)), [id]);
  const surveys = useLoad(() => api.get<Survey[]>('/api/surveys').catch(() => [] as Survey[]));
  const versions = useLoad(() => (isNew ? Promise.resolve([] as Version[]) : api.get<Version[]>(`/api/templates/${id}/versions`)), [id]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [dirty, setDirty] = useState(false);
  const [meta, setMeta] = useState<{ publishedVersion: number | null; hasUnpublishedChanges: boolean }>({ publishedVersion: null, hasUnpublishedChanges: true });
  const [detach, setDetach] = useState(false);

  useEffect(() => {
    if (isNew) {
      const starter = STARTERS.find((s) => s.key === params.get('starter')) ?? STARTERS[0];
      const design = starter.design();
      setDraft({ name: '', description: '', design, html: design ? compile(design, config.publicBaseUrl) : '<p>{DisplayName}</p>', imageMode: 'Hosted' });
      setDirty(true);
    } else if (existing.data) {
      const t = existing.data;
      let design: Design | null = null;
      try { design = t.designJson ? (JSON.parse(t.designJson) as Design) : null; } catch { /* fall back to source mode */ }
      setDraft({ name: t.name, description: t.description ?? '', html: t.html, design, imageMode: t.imageMode });
      setMeta({ publishedVersion: t.publishedVersion, hasUnpublishedChanges: t.hasUnpublishedChanges });
      setDirty(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [existing.data, isNew]);

  // Warn before losing unsaved work.
  useEffect(() => {
    if (!dirty) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [dirty]);

  const change = (patch: Partial<Draft>) => { setDraft((d) => (d ? { ...d, ...patch } : d)); setDirty(true); };
  const changeDesign = (design: Design) => change({ design, html: compile(design, config.publicBaseUrl) });

  const body = () => ({ name: draft!.name, description: draft!.description, html: draft!.html, designJson: draft!.design ? JSON.stringify(draft!.design) : null, imageMode: draft!.imageMode });
  const save = async (): Promise<string | null> => {
    let savedId: string | null = null;
    const ok = await run(async () => {
      const t = isNew ? await api.post<Template>('/api/templates', body()) : await api.put<Template>(`/api/templates/${id}`, body());
      savedId = t.id;
      setMeta({ publishedVersion: t.publishedVersion, hasUnpublishedChanges: t.hasUnpublishedChanges });
      setDirty(false);
      if (isNew) navigate(`/templates/${t.id}`, { replace: true });
    }, 'Draft saved');
    return ok ? savedId : null;
  };
  const publish = async () => {
    const target = dirty && canEdit ? await save() : id!;
    if (!target) return;
    await run(async () => {
      const { version } = await api.post<{ version: number }>(`/api/templates/${target}/publish`);
      setMeta({ publishedVersion: version, hasUnpublishedChanges: false });
      await versions.reload();
    }, 'Published — rules using this template now send the new version');
  };

  // ----- preview -----
  const [previewUser, setPreviewUser] = useState<string | null>(null);
  const [rendered, setRendered] = useState<Rendered | null>(null);
  const [renderError, setRenderError] = useState<string | null>(null);
  const [view, setView] = useState<'html' | 'text' | 'rtf'>('html');
  const [device, setDevice] = useState<'desktop' | 'mobile'>('desktop');
  const [darkPreview, setDarkPreview] = useState(false);
  const html = draft?.html;
  useEffect(() => {
    if (html == null) return;
    const handle = setTimeout(async () => {
      try {
        const r = await api.post<Rendered>('/api/templates/render', { html, userId: previewUser });
        setRendered(r);
        setRenderError(null);
        if (!previewUser) setPreviewUser(r.userId);
      } catch (e) {
        setRenderError((e as Error).message);
      }
    }, 350);
    return () => clearTimeout(handle);
  }, [html, previewUser]);

  const frameDoc = useMemo(() => {
    // Approximates Outlook's dark mode: dark canvas, light default text; explicitly coloured text keeps its colour.
    const style = darkPreview ? 'background:#1f1f1f;color:#f5f5f5' : 'background:#ffffff;color:#000000';
    return `<!doctype html><html><head><meta charset="utf-8"><base target="_blank"></head><body style="margin:16px;font-family:Segoe UI,Arial,sans-serif;font-size:14px;${style}">` +
      `<p style="opacity:.55">Hi Alex,<br><br>Thanks for your message — details below.</p>${rendered?.html ?? ''}</body></html>`;
  }, [rendered, darkPreview]);

  // ----- image library -----
  const [library, setLibrary] = useState(false);
  const pickResolve = useRef<((url: string | null) => void) | null>(null);
  const pickImage = () => new Promise<string | null>((resolve) => { pickResolve.current = resolve; setLibrary(true); });
  const closeLibrary = (url: string | null) => { pickResolve.current?.(url); pickResolve.current = null; setLibrary(false); };

  if (existing.error) return <Page title="Template"><LoadState loading={false} error={existing.error} onRetry={existing.reload} /></Page>;
  if (!draft) return <Spinner label="Loading template…" className="pad" />;

  return (
    <div className="page wide">
      <div className="designer-bar">
        <Input aria-label="Template name" value={draft.name} placeholder="Template name" onChange={(_, d) => change({ name: d.value })} readOnly={!canEdit} style={{ minWidth: 260, fontWeight: 600 }} />
        <Input aria-label="Description" value={draft.description} placeholder="Description (optional)" onChange={(_, d) => change({ description: d.value })} readOnly={!canEdit} className="grow" style={{ maxWidth: 420 }} />
        {meta.publishedVersion == null
          ? <Badge appearance="tint" color="warning">Never published</Badge>
          : <Badge appearance="tint" color={meta.hasUnpublishedChanges || dirty ? 'warning' : 'success'}>{meta.hasUnpublishedChanges || dirty ? `Live: v${meta.publishedVersion} · draft has changes` : `Live: v${meta.publishedVersion}`}</Badge>}
        <span className="right row">
          {!isNew && (versions.data?.length ?? 0) > 0 && (
            <Menu>
              <MenuTrigger disableButtonEnhancement><Button icon={<HistoryRegular />}>History</Button></MenuTrigger>
              <MenuPopover><MenuList>
                {versions.data!.map((v) => (
                  <MenuItem key={v.version} disabled={!canEdit} secondaryContent={fmtDate(v.publishedAt)}
                    onClick={() => void run(async () => { await api.post(`/api/templates/${id}/restore/${v.version}`); await existing.reload(); }, `Draft restored from v${v.version}`)}>
                    Restore v{v.version} — {v.publishedBy}
                  </MenuItem>
                ))}
              </MenuList></MenuPopover>
            </Menu>
          )}
          <Button onClick={() => navigate('/templates')}>Close</Button>
          {canEdit && <Button disabled={busy || !dirty || !draft.name.trim()} onClick={() => void save()}>Save draft</Button>}
          {canPublish && <Button appearance="primary" disabled={busy || !draft.name.trim() || (isNew && !canEdit) || (!dirty && !meta.hasUnpublishedChanges)} onClick={() => void publish()}>Publish</Button>}
        </span>
      </div>
      {canEdit && !canPublish && <MessageBar><MessageBarBody>Your changes are saved as a draft. A rule administrator publishes them to live mail.</MessageBarBody></MessageBar>}
      {!canEdit && <MessageBar><MessageBarBody>You can preview and publish this template; editing it requires the template designer role.</MessageBarBody></MessageBar>}

      <div className="designer">
        <section className="stack">
          <div className="row">
            <TabList selectedValue={draft.design ? 'design' : 'source'} onTabSelect={(_, d) => { if (d.value === 'source' && draft.design) setDetach(true); }}>
              <Tab value="design" disabled={!draft.design}>Design</Tab>
              <Tab value="source">HTML source</Tab>
            </TabList>
            <Field label="Images" orientation="horizontal" className="right">
              <RadioGroup layout="horizontal" value={draft.imageMode} onChange={(_, d) => change({ imageMode: d.value as Draft['imageMode'] })} disabled={!canEdit}>
                <Radio value="Hosted" label="Hosted (linked)" /><Radio value="Inline" label="Embedded in the email" />
              </RadioGroup>
            </Field>
          </div>
          {draft.design
            ? <BlockEditor design={draft.design} onChange={changeDesign} surveys={surveys.data ?? []} placeholders={config.placeholders} publicBaseUrl={config.publicBaseUrl} readOnly={!canEdit} pickImage={pickImage} />
            : (
              <>
                <Suspense fallback={<Spinner label="Loading editor…" className="pad" />}>
                  <SourceEditor value={draft.html} onChange={(v) => change({ html: v })} readOnly={!canEdit} dark={dark} />
                </Suspense>
                <div className="row">
                  {canEdit && <Button size="small" onClick={() => void pickImage().then((url) => { if (url) void navigator.clipboard.writeText(url); })}>Image library (copies URL)</Button>}
                  <Text size={200} className="muted">
                    Placeholders: <span className="mono">{'{FirstName} {JobTitle} {Photo} {QrCode} {CustomAttribute1}…'}</span> · conditional:{' '}
                    <span className="mono">{'{if MobilePhone}M: {MobilePhone}{/if}'}</span>
                  </Text>
                </div>
              </>
            )}
        </section>

        <section className="panel stack" style={{ position: 'sticky', top: 64 }}>
          <div className="row">
            <h2 style={{ margin: 0, fontSize: 15 }}>Preview</h2>
            <span className="right row" style={{ gap: 4 }}>
              <ToggleButton size="small" icon={<DesktopRegular />} checked={device === 'desktop'} onClick={() => setDevice('desktop')} aria-label="Desktop width" />
              <ToggleButton size="small" icon={<PhoneRegular />} checked={device === 'mobile'} onClick={() => setDevice('mobile')} aria-label="Phone width" />
              <ToggleButton size="small" icon={<WeatherMoonRegular />} checked={darkPreview} onClick={() => setDarkPreview((v) => !v)} aria-label="Dark mode" />
            </span>
          </div>
          <Field label="Preview as"><UserSelect ariaLabel="Preview as" value={previewUser} onChange={setPreviewUser} /></Field>
          <TabList size="small" selectedValue={view} onTabSelect={(_, d) => setView(d.value as typeof view)}>
            <Tab value="html">HTML</Tab><Tab value="text">Plain text</Tab><Tab value="rtf">RTF</Tab>
          </TabList>
          {renderError && <MessageBar intent="error"><MessageBarBody>{renderError}</MessageBarBody></MessageBar>}
          {view === 'html' && (
            <div className={`preview-stage ${device}`}>
              <iframe className="preview-frame" title="Signature preview" sandbox="allow-popups" srcDoc={frameDoc} style={{ height: 420 }} />
            </div>
          )}
          {view === 'text' && <pre className="preview-text" tabIndex={0}>{rendered?.text}</pre>}
          {view === 'rtf' && (
            <>
              <Text size={200} className="muted">Generated automatically for rich-text (RTF) messages.</Text>
              <div><Button size="small" disabled={!rendered} onClick={() => download(`${draft.name || 'signature'}.rtf`, rendered!.rtf, 'application/rtf')}>Download .rtf</Button></div>
              <pre className="preview-text" tabIndex={0}>{rendered?.rtf}</pre>
            </>
          )}
        </section>
      </div>

      <ConfirmDialog open={detach} title="Switch to HTML source?" confirmLabel="Edit as HTML" onClose={() => setDetach(false)}
        body="You can edit the generated HTML directly, but this template can then no longer be opened in the visual designer."
        onConfirm={() => change({ design: null })} />
      {library && <ImageLibrary canEdit={canEdit} onClose={closeLibrary} />}
    </div>
  );
}

function ImageLibrary({ onClose, canEdit }: { onClose: (url: string | null) => void; canEdit: boolean }) {
  const assets = useLoad(() => api.get<Asset[]>('/api/assets'));
  const { run, busy } = useAction();
  const input = useRef<HTMLInputElement>(null);
  const [deleting, setDeleting] = useState<Asset | null>(null);
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Image library</DialogTitle>
          <DialogContent className="stack">
            <LoadState loading={assets.loading && !assets.data} error={assets.error} onRetry={assets.reload} />
            {assets.data?.length === 0 && <Text className="muted">No images yet. Upload a logo or banner (PNG, JPEG or GIF, up to 2 MB).</Text>}
            <div className="asset-grid">
              {assets.data?.map((a) => (
                <div key={a.id} style={{ position: 'relative' }}>
                  <button type="button" className="asset" style={{ width: '100%' }} onClick={() => onClose(a.url)} title={`Use ${a.fileName}`}>
                    <img src={new URL(a.url).pathname} alt="" /><span>{a.fileName}</span>
                  </button>
                  {canEdit && <Button size="small" appearance="subtle" icon={<DeleteRegular />} aria-label={`Delete ${a.fileName}`} style={{ position: 'absolute', top: 0, right: 0 }} onClick={() => setDeleting(a)} />}
                </div>
              ))}
            </div>
            <input ref={input} type="file" accept="image/png,image/jpeg,image/gif" hidden
              onChange={(e) => {
                const file = e.target.files?.[0];
                e.target.value = '';
                if (file) void run(async () => { await api.upload('/api/assets', file); await assets.reload(); }, 'Image uploaded');
              }} />
          </DialogContent>
          <DialogActions>
            {canEdit && <Button icon={<ArrowUploadRegular />} disabled={busy} onClick={() => input.current?.click()}>Upload image</Button>}
            <Button appearance="primary" onClick={() => onClose(null)}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
      <ConfirmDialog open={deleting != null} title="Delete image?" confirmLabel="Delete image" onClose={() => setDeleting(null)}
        body={<>“{deleting?.fileName}” will disappear from every signature that links to it, including emails already sent with hosted images.</>}
        onConfirm={() => void run(async () => { await api.del(`/api/assets/${deleting!.id}`); await assets.reload(); }, 'Image deleted')} />
    </Dialog>
  );
}
