import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import type { JobStatus } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Spinner } from './ui'

/** Follows a background task (build, publish, deploy) and shows its live log. */
export function JobView({ jobId, onDone }: { jobId: string; onDone?: (job: JobStatus) => void }) {
  const [job, setJob] = useState<JobStatus | null>(null)
  const [showLog, setShowLog] = useState(true)
  const logRef = useRef<HTMLDivElement>(null)
  const doneRef = useRef(false)

  useEffect(() => {
    let alive = true
    let timer: number
    doneRef.current = false
    const tick = async () => {
      try {
        const j = await api.job(jobId)
        if (!alive) return
        setJob(j)
        if (j.state === 'running') timer = window.setTimeout(tick, 1200)
        else if (!doneRef.current) { doneRef.current = true; onDone?.(j) }
      } catch { timer = window.setTimeout(tick, 3000) }
    }
    tick()
    return () => { alive = false; clearTimeout(timer) }
  }, [jobId]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { logRef.current?.scrollTo({ top: logRef.current.scrollHeight }) }, [job?.log.length])

  if (!job) return <div className="row small subtle"><Spinner /> Starting…</div>
  const running = job.state === 'running'
  return (
    <div className="col gap-6">
      <div className="row wrap">
        {running ? <Spinner /> : <Icon name={job.state === 'succeeded' ? 'checkCircle' : 'alert'} size={18} />}
        <span className="strong grow">{job.title}</span>
        {running && <Button size="sm" variant="ghost" icon="stop" onClick={() => api.cancelJob(job.id)}>Cancel</Button>}
        <Button size="sm" variant="ghost" icon="fileText" onClick={() => setShowLog(s => !s)}>{showLog ? 'Hide log' : 'Show log'}</Button>
      </div>
      {!running && job.result && (
        <Alert kind={job.state === 'succeeded' ? 'success' : job.state === 'cancelled' ? 'warning' : 'error'}>
          {job.result} {job.resultUrl && <a href={job.resultUrl} target="_blank" rel="noreferrer" className="strong">Open {job.resultUrl}</a>}
        </Alert>
      )}
      {showLog && <div ref={logRef} className="log" style={{ maxHeight: 320 }}>{job.log.join('\n') || 'Waiting for output…'}</div>}
    </div>
  )
}
