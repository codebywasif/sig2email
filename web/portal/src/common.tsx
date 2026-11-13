import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Input, MessageBar, MessageBarBody,
  Option, Spinner, Tag, TagGroup, Text, Title3, Toast, ToastTitle, useToastController,
} from '@fluentui/react-components';
import { api, type DirectoryGroup, type DirectoryUser } from './api';

export const TOASTER_ID = 'app-toaster';

/** Loads data on mount / when deps change; exposes reload and error state. */
export function useLoad<T>(loader: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const seq = useRef(0);
  const reload = useCallback(async () => {
    const mine = ++seq.current;
    setLoading(true);
    try {
      const result = await loader();
      if (mine === seq.current) { setData(result); setError(null); }
    } catch (e) {
      if (mine === seq.current) setError((e as Error).message);
    } finally {
      if (mine === seq.current) setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(() => { void reload(); }, [reload]);
  return { data, error, loading, reload, setData };
}

export function useNotify() {
  const { dispatchToast } = useToastController(TOASTER_ID);
  return useCallback((message: string, intent: 'success' | 'error' | 'info' = 'success') =>
    dispatchToast(<Toast><ToastTitle>{message}</ToastTitle></Toast>, { intent, timeout: intent === 'error' ? 8000 : 3500 }), [dispatchToast]);
}

/** Runs an action, toasts success or the error message. Returns true on success. */
export function useAction() {
  const notify = useNotify();
  const [busy, setBusy] = useState(false);
  const run = useCallback(async (action: () => Promise<unknown>, success?: string) => {
    setBusy(true);
    try {
      await action();
      if (success) notify(success);
      return true;
    } catch (e) {
      notify((e as Error).message, 'error');
      return false;
    } finally {
      setBusy(false);
    }
  }, [notify]);
  return { run, busy };
}

export function Page({ title, description, actions, children }: { title: string; description?: string; actions?: ReactNode; children: ReactNode }) {
  useEffect(() => { document.title = `${title} · Sig2Email`; }, [title]);
  return (
    <div className="page">
      <header className="page-header">
        <div>
          <Title3 as="h1">{title}</Title3>
          {description && <Text as="p" className="muted">{description}</Text>}
        </div>
        {actions && <div className="page-actions">{actions}</div>}
      </header>
      {children}
    </div>
  );
}

export function LoadState({ loading, error, onRetry }: { loading: boolean; error: string | null; onRetry?: () => void }) {
  if (error) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error} {onRetry && <Button size="small" appearance="transparent" onClick={onRetry}>Try again</Button>}</MessageBarBody>
      </MessageBar>
    );
  }
  return loading ? <Spinner label="Loading…" className="pad" /> : null;
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return <div className="empty"><Text weight="semibold" size={400}>{title}</Text>{children && <Text className="muted">{children}</Text>}</div>;
}

export function ConfirmDialog({ open, title, body, confirmLabel, onConfirm, onClose }: {
  open: boolean; title: string; body: ReactNode; confirmLabel: string; onConfirm: () => void; onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>{body}</DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>Cancel</Button>
            <Button appearance="primary" onClick={() => { onConfirm(); onClose(); }}>{confirmLabel}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** Free-text chips: type and press Enter or comma. */
export function TagInput({ values, onChange, placeholder, ariaLabel }: { values: string[]; onChange: (v: string[]) => void; placeholder?: string; ariaLabel: string }) {
  const [draft, setDraft] = useState('');
  const commit = () => {
    const parts = draft.split(',').map((s) => s.trim()).filter((s) => s && !values.includes(s));
    if (parts.length) onChange([...values, ...parts]);
    setDraft('');
  };
  return (
    <div className="tag-input">
      {values.length > 0 && (
        <TagGroup aria-label={ariaLabel} onDismiss={(_, d) => onChange(values.filter((v) => v !== d.value))}>
          {values.map((v) => <Tag key={v} value={v} dismissible dismissIcon={{ 'aria-label': `Remove ${v}` }} size="small">{v}</Tag>)}
        </TagGroup>
      )}
      <Input value={draft} placeholder={placeholder} aria-label={ariaLabel} onChange={(_, d) => setDraft(d.value)} onBlur={commit}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); commit(); } }} />
    </div>
  );
}

interface PickItem { id: string; label: string; detail?: string }

/** Multi-select with server-side search over directory users or groups. */
function DirectoryPicker({ kind, values, onChange, ariaLabel, known }: {
  kind: 'users' | 'groups'; values: string[]; onChange: (v: string[]) => void; ariaLabel: string; known?: PickItem[];
}) {
  const [query, setQuery] = useState('');
  const [options, setOptions] = useState<PickItem[]>([]);
  const [names, setNames] = useState<Record<string, string>>({});

  useEffect(() => {
    if (known) { setOptions(known.filter((k) => k.label.toLowerCase().includes(query.toLowerCase()))); return; }
    const handle = setTimeout(async () => {
      try {
        if (kind === 'users') {
          const r = await api.get<{ items: DirectoryUser[] }>(`/api/directory/users?take=20&search=${encodeURIComponent(query)}`);
          setOptions(r.items.map((u) => ({ id: u.id, label: u.displayName, detail: [u.jobTitle, u.department].filter(Boolean).join(' · ') })));
        } else {
          const r = await api.get<DirectoryGroup[]>(`/api/directory/groups?search=${encodeURIComponent(query)}`);
          setOptions(r.slice(0, 30).map((g) => ({ id: g.id, label: g.displayName, detail: `${g.kind} · ${g.memberCount} direct members` })));
        }
      } catch { setOptions([]); }
    }, 200);
    return () => clearTimeout(handle);
  }, [query, kind, known]);

  useEffect(() => {
    setNames((n) => ({ ...n, ...Object.fromEntries([...(known ?? []), ...options].map((o) => [o.id, o.label])) }));
  }, [options, known]);

  // Resolve names for ids selected before this picker was opened.
  useEffect(() => {
    if (known) return;
    const missing = values.filter((v) => !(v in names));
    if (missing.length === 0) return;
    void (async () => {
      const resolved: Record<string, string> = {};
      if (kind === 'groups') {
        const all = await api.get<DirectoryGroup[]>('/api/directory/groups').catch(() => []);
        for (const g of all) resolved[g.id] = g.displayName;
      } else {
        await Promise.all(missing.map(async (id) => {
          const r = await api.get<{ user: DirectoryUser }>(`/api/directory/users/${encodeURIComponent(id)}`).catch(() => null);
          resolved[id] = r?.user.displayName ?? `Unknown (${id})`;
        }));
      }
      for (const id of missing) resolved[id] ??= `Unknown (${id})`;
      setNames((n) => ({ ...n, ...resolved }));
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [values, kind]);

  return (
    <div className="tag-input">
      {values.length > 0 && (
        <TagGroup aria-label={`Selected ${ariaLabel}`} onDismiss={(_, d) => onChange(values.filter((v) => v !== d.value))}>
          {values.map((v) => <Tag key={v} value={v} dismissible dismissIcon={{ 'aria-label': `Remove ${names[v] ?? v}` }} size="small">{names[v] ?? '…'}</Tag>)}
        </TagGroup>
      )}
      <Combobox aria-label={ariaLabel} placeholder={kind === 'users' ? 'Search people…' : 'Search groups…'} freeform value={query}
        onChange={(e) => setQuery(e.target.value)} selectedOptions={[]}
        onOptionSelect={(_, d) => { if (d.optionValue && !values.includes(d.optionValue)) onChange([...values, d.optionValue]); setQuery(''); }}>
        {options.filter((o) => !values.includes(o.id)).map((o) => (
          <Option key={o.id} value={o.id} text={o.label}>
            <span>{o.label}{o.detail && <Text size={200} className="muted"> — {o.detail}</Text>}</span>
          </Option>
        ))}
        {options.length === 0 && <Option disabled value="" text="">No matches</Option>}
      </Combobox>
    </div>
  );
}

export const UserPicker = (p: { values: string[]; onChange: (v: string[]) => void; ariaLabel: string; known?: PickItem[] }) => <DirectoryPicker kind="users" {...p} />;
export const GroupPicker = (p: { values: string[]; onChange: (v: string[]) => void; ariaLabel: string; known?: PickItem[] }) => <DirectoryPicker kind="groups" {...p} />;

/** Single directory user selector (preview-as, simulator sender). */
export function UserSelect({ value, onChange, ariaLabel }: { value: string | null; onChange: (id: string) => void; ariaLabel: string }) {
  const { data } = useLoad(() => api.get<{ items: DirectoryUser[] }>('/api/directory/users?take=200'));
  const users = data?.items ?? [];
  const current = users.find((u) => u.id === value);
  return (
    <Dropdown aria-label={ariaLabel} placeholder="Choose a person" value={current?.displayName ?? ''} selectedOptions={value ? [value] : []}
      onOptionSelect={(_, d) => { if (d.optionValue) onChange(d.optionValue); }}>
      {users.map((u) => <Option key={u.id} value={u.id} text={u.displayName}>{u.displayName}<Text size={200} className="muted"> — {u.department ?? 'No department'}</Text></Option>)}
    </Dropdown>
  );
}

export const fmtDate = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—');
export const fmtDay = (iso: string) => new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' });

/** ISO (UTC) ⇄ value of an <input type="datetime-local"> in the viewer's time zone. */
export const toLocalInput = (iso: string | null) => {
  if (!iso) return '';
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
};
export const fromLocalInput = (value: string) => (value ? new Date(value).toISOString() : null);

export function download(filename: string, content: string, type = 'text/plain') {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}
