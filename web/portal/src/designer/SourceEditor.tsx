import Editor, { loader } from '@monaco-editor/react';
import * as monaco from 'monaco-editor';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import htmlWorker from 'monaco-editor/esm/vs/language/html/html.worker?worker';
import cssWorker from 'monaco-editor/esm/vs/language/css/css.worker?worker';

// Monaco is bundled with the portal (no CDN), including its language workers.
self.MonacoEnvironment = {
  getWorker(_id, label) {
    if (label === 'html' || label === 'handlebars' || label === 'razor') return new htmlWorker();
    if (label === 'css' || label === 'scss' || label === 'less') return new cssWorker();
    return new editorWorker();
  },
};
loader.config({ monaco });

export default function SourceEditor({ value, onChange, readOnly, dark }: { value: string; onChange: (v: string) => void; readOnly?: boolean; dark: boolean }) {
  return (
    <div className="monaco-host">
      <Editor language="html" value={value} theme={dark ? 'vs-dark' : 'vs'} onChange={(v) => onChange(v ?? '')}
        options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 13, readOnly, scrollBeyondLastLine: false, automaticLayout: true, tabSize: 2, ariaLabel: 'Template HTML source' }} />
    </div>
  );
}
