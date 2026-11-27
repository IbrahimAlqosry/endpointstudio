import { useMemo, useState } from 'react'
import { api } from '../api'
import type { TestCaseResult, TestRunResult } from '../types'
import { Icon } from './Icon'
import { JobView } from './JobView'
import { Alert, Button, Empty, errorText, useToast } from './ui'

/** Runs the tests and shows each one, failures first. */
export function TestsPanel({ path, npm }: { path: string; npm?: boolean }) {
  const toast = useToast()
  const [job, setJob] = useState<string | null>(null)
  const [result, setResult] = useState<TestRunResult | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [show, setShow] = useState<'all' | 'failed'>('all')

  const run = async (filter?: string, noBuild = false) => {
    setResult(null)
    try { setJob((await api.testsRun(path, filter, noBuild)).jobId) } catch (e) { toast.error(errorText(e)) }
  }

  const groups = useMemo(() => {
    const m = new Map<string, TestCaseResult[]>()
    for (const t of result?.tests ?? []) if (show === 'all' || t.outcome === 'failed') m.set(t.className || '(tests)', [...(m.get(t.className || '(tests)') ?? []), t])
    return [...m.entries()]
  }, [result, show])

  const failedFilter = result?.tests.filter(t => t.outcome === 'failed').map(t => `FullyQualifiedName~${t.className}.${t.name.replace(/\(.*$/, '')}`).join('|')

  return (
    <div className="col gap-12">
      <div className="row wrap gap-6">
        <Button variant="primary" icon="play" onClick={() => run()}>Run all tests</Button>
        {!npm && result && result.failed > 0 && <Button icon="repeat" onClick={() => run(failedFilter, true)}>Run failed again</Button>}
        <div className="grow" />
        {result && result.total > 0 && (
          <div className="segmented">
            <button className={show === 'all' ? 'active' : ''} onClick={() => setShow('all')}>All ({result.total})</button>
            <button className={show === 'failed' ? 'active' : ''} onClick={() => setShow('failed')}>Failed ({result.failed})</button>
          </div>
        )}
      </div>
      {!job && <div className="card"><Empty icon="checkCircle" title="Run the tests">{npm ? 'Runs the app’s test script.' : 'Finds the test projects (xUnit, NUnit, MSTest), builds them and shows every test.'}</Empty></div>}
      {job && <div className="card pad"><JobView key={job} jobId={job} onDone={async () => { if (!npm) { const r = await api.testResults(job).catch(() => null); setResult(r) } }} /></div>}
      {result && result.total > 0 && (
        <>
          <div className="stats" style={{ marginBottom: 0 }}>
            <div className="card stat"><div className="stat-value">{result.total}</div><div className="stat-label">Tests</div></div>
            <div className="card stat"><div className="stat-value" style={{ color: 'var(--success)' }}>{result.passed}</div><div className="stat-label">Passed</div></div>
            <div className="card stat"><div className="stat-value" style={{ color: result.failed ? 'var(--danger)' : undefined }}>{result.failed}</div><div className="stat-label">Failed</div></div>
            <div className="card stat"><div className="stat-value">{(result.durationMs / 1000).toFixed(1)}s</div><div className="stat-label">Time</div></div>
          </div>
          {groups.map(([cls, tests]) => (
            <div key={cls} className="card">
              <div className="git-branch"><Icon name="layers" size={14} /><span className="strong grow">{cls}</span>
                <span className="tiny subtle">{tests.filter(t => t.outcome === 'passed').length}/{tests.length} passed</span>
                {!npm && <Button size="sm" variant="ghost" icon="play" onClick={() => run(`FullyQualifiedName~${cls}`, true)}>Run</Button>}
              </div>
              {tests.map(t => {
                const key = cls + t.name
                return (
                  <div key={key}>
                    <div className="git-branch" style={{ cursor: t.message || t.output ? 'pointer' : 'default', paddingLeft: 30 }} onClick={() => setOpen(open === key ? null : key)}>
                      <Icon name={t.outcome === 'passed' ? 'checkCircle' : t.outcome === 'failed' ? 'alert' : 'ban'} size={14} />
                      <span className="grow mono small" style={{ color: t.outcome === 'failed' ? 'var(--danger)' : undefined }}>{t.name}</span>
                      <span className="tiny subtle">{t.durationMs < 1 ? '<1' : Math.round(t.durationMs)} ms</span>
                      {!npm && <Button size="sm" variant="ghost" iconOnly icon="play" title="Run this test" onClick={e => { e.stopPropagation(); run(`FullyQualifiedName~${cls}.${t.name.replace(/\(.*$/, '')}`, true) }} />}
                    </div>
                    {open === key && (t.message || t.output) && (
                      <div style={{ padding: '0 14px 12px 30px' }}>
                        {t.message && <Alert kind="error"><pre style={{ margin: 0, whiteSpace: 'pre-wrap', font: 'inherit' }}>{t.message}</pre></Alert>}
                        {t.stackTrace && <pre className="log" style={{ marginTop: 8, whiteSpace: 'pre-wrap', maxHeight: 240 }}>{t.stackTrace}</pre>}
                        {t.output && <pre className="log" style={{ marginTop: 8, whiteSpace: 'pre-wrap', maxHeight: 200 }}>{t.output}</pre>}
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          ))}
        </>
      )}
    </div>
  )
}
