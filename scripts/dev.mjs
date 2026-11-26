// Starts the .NET helper service and the Vite dev server together.
import { spawn } from 'node:child_process'

const procs = []
const run = (name, cmd, args, color) => {
  const p = spawn(cmd, args, { shell: true, stdio: ['ignore', 'pipe', 'pipe'] })
  const prefix = `\x1b[${color}m[${name}]\x1b[0m `
  const out = d => process.stdout.write(d.toString().split(/\r?\n/).filter(Boolean).map(l => prefix + l).join('\n') + '\n')
  p.stdout.on('data', out)
  p.stderr.on('data', out)
  p.on('exit', code => { console.log(`${prefix}exited with code ${code}`); shutdown() })
  procs.push(p)
}

let stopping = false
function shutdown() {
  if (stopping) return
  stopping = true
  for (const p of procs) {
    if (p.exitCode === null) {
      if (process.platform === 'win32') spawn('taskkill', ['/pid', String(p.pid), '/T', '/F'])
      else p.kill('SIGTERM')
    }
  }
  setTimeout(() => process.exit(0), 500)
}
process.on('SIGINT', shutdown)
process.on('SIGTERM', shutdown)

run('server', 'dotnet', ['run', '--project', 'server'], '35')
run('ui', 'npx', ['vite'], '36')
