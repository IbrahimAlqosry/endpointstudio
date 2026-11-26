import { useEffect, useState } from 'react'
import { api } from '../api'
import type { DeployProfile, DeployRequest, DeployTarget, DeployTools, ProjectDetails } from '../types'
import { FolderPicker } from './FolderPicker'
import { Icon } from './Icon'
import { JobView } from './JobView'
import { Alert, Button, Choice, Field, Switch, errorText, timeAgo, useToast } from './ui'

const TARGETS: { id: DeployTarget; title: string; desc: string; icon: string; needs?: keyof DeployTools }[] = [
  { id: 'folder', title: 'Folder or network share', desc: 'Copy to a folder, an IIS site folder or \\\\server\\share. IIS sites are taken offline while files are replaced.', icon: 'folder' },
  { id: 'zip', title: 'ZIP package', desc: 'One file you can send or upload anywhere.', icon: 'download' },
  { id: 'docker', title: 'Docker image', desc: 'Build an image, run it on this computer and/or push it to a registry.', icon: 'box', needs: 'docker' },
  { id: 'dockerRemote', title: 'Docker on a server', desc: 'Build here, send the image to a Linux server over SSH and run it there.', icon: 'server', needs: 'docker' },
  { id: 'ssh', title: 'Linux server (SSH)', desc: 'Copy the files to a folder on a server and restart the service.', icon: 'monitor', needs: 'ssh' },
  { id: 'webdeploy', title: 'IIS with Web Deploy', desc: 'Publish to a remote Windows server running IIS.', icon: 'layers', needs: 'msDeploy' },
  { id: 'azure', title: 'Azure App Service', desc: 'Upload to an existing Azure web app (uses the Azure CLI).', icon: 'rocket', needs: 'azureCli' },
  { id: 'ftp', title: 'FTP / FTPS hosting', desc: 'Upload to shared hosting such as Plesk or cPanel.', icon: 'send' },
]
const RUNTIMES = [
  { value: '', label: 'Any (needs .NET installed on the server)' },
  { value: 'win-x64', label: 'Windows 64-bit' }, { value: 'linux-x64', label: 'Linux 64-bit' },
  { value: 'linux-arm64', label: 'Linux ARM (e.g. Raspberry Pi, Graviton)' }, { value: 'osx-arm64', label: 'macOS (Apple silicon)' },
]

export function DeployPanel({ project }: { project: ProjectDetails }) {
  const toast = useToast()
  const [tools, setTools] = useState<DeployTools | null>(null)
  const [profiles, setProfiles] = useState<DeployProfile[]>([])
  const [req, setReq] = useState<DeployRequest>({ target: 'folder', configuration: 'Release', imageTag: 'latest', hostPort: 8080, ftps: true })
  const [jobId, setJobId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [picker, setPicker] = useState(false)
  const framework = project.capabilities.platform === 'NetFramework'

  useEffect(() => {
    api.deployTools().then(setTools).catch(() => {})
    api.deployProfiles(project.path).then(setProfiles).catch(() => {})
  }, [project.path])

  const set = (patch: Partial<DeployRequest>) => setReq(r => ({ ...r, ...patch }))
  const target = TARGETS.find(t => t.id === req.target)!
  const missingTool = target.needs && tools && !tools[target.needs]
  const dockerStopped = (req.target === 'docker' || req.target === 'dockerRemote') && tools?.docker && !tools.dockerRunning
  const blockedByFramework = framework && (req.target === 'docker' || req.target === 'dockerRemote')

  const start = async (r: DeployRequest) => {
    setBusy(true)
    try {
      const { jobId } = await api.deploy(project.path, r)
      setJobId(jobId)
      api.deployProfiles(project.path).then(setProfiles).catch(() => {})
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(false) }
  }

  return (
    <div className="col gap-20">
      {profiles.length > 0 && (
        <div className="card pad">
          <div className="row gap-6" style={{ marginBottom: 10 }}><Icon name="history" size={15} /><span className="strong">Publish again</span><span className="tiny subtle">Saved from earlier runs. Passwords are never saved.</span></div>
          <div className="row wrap gap-6">
            {profiles.map(p => (
              <div key={p.id} className="row gap-4" style={{ padding: '6px 6px 6px 12px', border: '1px solid var(--border)', borderRadius: 10 }}>
                <Icon name={TARGETS.find(t => t.id === p.request.target)?.icon ?? 'send'} size={15} />
                <span className="small strong">{p.name}</span>
                <span className="tiny subtle">{timeAgo(p.saved)}</span>
                <Button size="sm" variant="ghost" onClick={() => setReq({ ...p.request })}>Edit</Button>
                <Button size="sm" variant="primary" icon="rocket" disabled={busy || !!p.request.push && !p.request.registryPassword && false} onClick={() => {
                  if (['webdeploy', 'ftp'].includes(p.request.target)) { setReq({ ...p.request }); toast.info('Enter the password, then publish.'); return }
                  start(p.request)
                }}>Publish</Button>
              </div>
            ))}
          </div>
        </div>
      )}

      <section>
        <div className="section-title"><Icon name="rocket" size={17} /> Where should it go?</div>
        <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))' }}>
          {TARGETS.map(t => {
            const unavailable = t.needs && tools && !tools[t.needs]
            return <Choice key={t.id} icon={t.icon} title={t.title} desc={t.desc + (unavailable ? ' (needs a tool that isn\'t installed)' : '')} selected={req.target === t.id} onClick={() => set({ target: t.id })} />
          })}
        </div>
      </section>

      <div className="card pad col gap-16">
        <div className="row gap-6"><Icon name={target.icon} size={16} /><span className="strong">{target.title}</span></div>
        {missingTool && <Alert kind="warning">{toolHelp(target.needs!)}</Alert>}
        {dockerStopped && <Alert kind="warning">Docker is installed but not running. Start Docker Desktop, then publish.</Alert>}
        {blockedByFramework && <Alert kind="warning">.NET Framework apps need Windows containers, which aren't supported here. Use a folder, ZIP, IIS or FTP instead.</Alert>}

        {(req.target === 'folder' || req.target === 'zip') && (
          <Field label={req.target === 'zip' ? 'Save the ZIP in' : 'Folder'} hint={req.target === 'folder' ? 'A local folder, an IIS site folder (e.g. C:\\inetpub\\wwwroot\\api) or a network share.' : 'A folder (the file is named automatically) or a full path ending in .zip.'}>
            <div className="row gap-6">
              <input className="input grow" value={req.outputPath ?? ''} onChange={e => set({ outputPath: e.target.value })} placeholder={req.target === 'zip' ? 'C:\\Releases' : 'C:\\inetpub\\wwwroot\\my-api'} />
              <Button icon="folder" onClick={() => setPicker(true)}>Browse</Button>
            </div>
          </Field>
        )}

        {(req.target === 'docker' || req.target === 'dockerRemote') && (
          <div className="grid-2">
            <Field label="Image name" hint="Lowercase. Leave empty to use the project name."><input className="input mono" value={req.imageName ?? ''} onChange={e => set({ imageName: e.target.value.toLowerCase() })} placeholder={project.name.toLowerCase().replace(/\./g, '-')} /></Field>
            <Field label="Version tag"><input className="input mono" value={req.imageTag ?? ''} onChange={e => set({ imageTag: e.target.value.toLowerCase() })} placeholder="latest" /></Field>
          </div>
        )}
        {req.target === 'docker' && (
          <>
            <Switch checked={!!req.runLocally} onChange={v => set({ runLocally: v })} label="Run it on this computer after building" />
            {req.runLocally && (
              <div className="grid-2">
                <Field label="Port on this computer"><input className="input" inputMode="numeric" value={req.hostPort ?? ''} onChange={e => set({ hostPort: Number(e.target.value.replace(/\D/g, '')) || null })} /></Field>
                <Field label="Container name" hint="Optional."><input className="input mono" value={req.containerName ?? ''} onChange={e => set({ containerName: e.target.value.toLowerCase() })} /></Field>
              </div>
            )}
            <Switch checked={!!req.push} onChange={v => set({ push: v })} label="Push it to a registry (Docker Hub, Azure, GitHub…)" />
            {req.push && (
              <div className="grid-3">
                <Field label="Registry" hint="e.g. docker.io/yourname"><input className="input mono" value={req.registry ?? ''} onChange={e => set({ registry: e.target.value })} /></Field>
                <Field label="User" hint="Leave empty if already signed in."><input className="input" value={req.registryUser ?? ''} onChange={e => set({ registryUser: e.target.value })} /></Field>
                <Field label="Password or token"><input className="input" type="password" value={req.registryPassword ?? ''} onChange={e => set({ registryPassword: e.target.value })} /></Field>
              </div>
            )}
          </>
        )}

        {(req.target === 'dockerRemote' || req.target === 'ssh') && (
          <>
            <div className="grid-3">
              <Field label="Server"><input className="input" value={req.host ?? ''} onChange={e => set({ host: e.target.value.trim() })} placeholder="203.0.113.10 or api.company.com" /></Field>
              <Field label="SSH user"><input className="input" value={req.user ?? ''} onChange={e => set({ user: e.target.value.trim() })} placeholder="deploy" /></Field>
              <Field label="SSH port" hint="Usually 22."><input className="input" inputMode="numeric" value={req.port ?? ''} onChange={e => set({ port: Number(e.target.value.replace(/\D/g, '')) || null })} placeholder="22" /></Field>
            </div>
            <Field label="SSH key file" hint="Optional. Your default key (~/.ssh/id_ed25519 or id_rsa) is used otherwise. Password sign-in isn't supported."><input className="input mono" value={req.keyFile ?? ''} onChange={e => set({ keyFile: e.target.value })} /></Field>
          </>
        )}
        {req.target === 'dockerRemote' && (
          <div className="grid-2">
            <Field label="Port on the server"><input className="input" inputMode="numeric" value={req.hostPort ?? ''} onChange={e => set({ hostPort: Number(e.target.value.replace(/\D/g, '')) || null })} /></Field>
            <Field label="Container name" hint="Optional."><input className="input mono" value={req.containerName ?? ''} onChange={e => set({ containerName: e.target.value.toLowerCase() })} /></Field>
          </div>
        )}
        {req.target === 'ssh' && (
          <div className="grid-2">
            <Field label="Folder on the server"><input className="input mono" value={req.remotePath ?? ''} onChange={e => set({ remotePath: e.target.value })} placeholder="/var/www/my-api" /></Field>
            <Field label="Command to run afterwards" hint="Optional, e.g. sudo systemctl restart my-api"><input className="input mono" value={req.restartCommand ?? ''} onChange={e => set({ restartCommand: e.target.value })} /></Field>
          </div>
        )}

        {req.target === 'webdeploy' && (
          <div className="grid-2">
            <Field label="Server" hint="Address of the IIS server (port 8172 is used)."><input className="input" value={req.host ?? ''} onChange={e => set({ host: e.target.value.trim() })} /></Field>
            <Field label="IIS site name"><input className="input" value={req.siteName ?? ''} onChange={e => set({ siteName: e.target.value })} placeholder="Default Web Site/api" /></Field>
            <Field label="User"><input className="input" value={req.user ?? ''} onChange={e => set({ user: e.target.value.trim() })} /></Field>
            <Field label="Password"><input className="input" type="password" value={req.password ?? ''} onChange={e => set({ password: e.target.value })} /></Field>
          </div>
        )}
        {req.target === 'azure' && (
          <div className="grid-2">
            <Field label="Resource group"><input className="input" value={req.resourceGroup ?? ''} onChange={e => set({ resourceGroup: e.target.value })} /></Field>
            <Field label="App Service name"><input className="input" value={req.appName ?? ''} onChange={e => set({ appName: e.target.value })} /></Field>
          </div>
        )}
        {req.target === 'ftp' && (
          <>
            <div className="grid-3">
              <Field label="FTP server"><input className="input" value={req.host ?? ''} onChange={e => set({ host: e.target.value.trim() })} placeholder="ftp.example.com" /></Field>
              <Field label="User"><input className="input" value={req.user ?? ''} onChange={e => set({ user: e.target.value.trim() })} /></Field>
              <Field label="Password"><input className="input" type="password" value={req.password ?? ''} onChange={e => set({ password: e.target.value })} /></Field>
            </div>
            <div className="grid-2">
              <Field label="Folder on the server" hint="e.g. /httpdocs or /site/wwwroot"><input className="input mono" value={req.remotePath ?? ''} onChange={e => set({ remotePath: e.target.value })} /></Field>
              <Field label="Security"><Switch checked={!!req.ftps} onChange={v => set({ ftps: v })} label="Use encrypted FTPS" /></Field>
            </div>
          </>
        )}

        <details className="disclose">
          <summary><Icon name="chevronRight" size={13} /> Build options</summary>
          <div className="grid-3">
            <Field label="Configuration">
              <select className="select" value={req.configuration ?? 'Release'} onChange={e => set({ configuration: e.target.value })}>
                <option value="Release">Release (recommended)</option><option value="Debug">Debug</option>
              </select>
            </Field>
            {!framework && req.target !== 'docker' && req.target !== 'dockerRemote' && (
              <Field label="Target system">
                <select className="select" value={req.runtime ?? ''} onChange={e => set({ runtime: e.target.value || null })}>
                  {RUNTIMES.map(r => <option key={r.value} value={r.value}>{r.label}</option>)}
                </select>
              </Field>
            )}
            {(req.target === 'docker' || req.target === 'dockerRemote') && (
              <Field label="Environment" hint="Sets ASPNETCORE_ENVIRONMENT.">
                <input className="input" value={req.environment ?? ''} onChange={e => set({ environment: e.target.value })} placeholder="Production" />
              </Field>
            )}
          </div>
          {req.runtime && !framework && (
            <div className="row wrap gap-20 mt-8">
              <Switch checked={!!req.selfContained} onChange={v => set({ selfContained: v })} label="Include .NET (no install needed on the server)" />
              <Switch checked={!!req.singleFile} onChange={v => set({ singleFile: v })} label="Single file" />
            </div>
          )}
        </details>

        <div className="row" style={{ justifyContent: 'flex-end' }}>
          <Button variant="primary" size="lg" icon="rocket" loading={busy} disabled={!!missingTool || !!blockedByFramework} onClick={() => start(req)}>Publish</Button>
        </div>
      </div>

      {jobId && <div className="card pad"><JobView key={jobId} jobId={jobId} /></div>}
      {picker && <FolderPicker mode="folder" initialPath={req.outputPath} onPick={p => { set({ outputPath: p }); setPicker(false) }} onClose={() => setPicker(false)} />}
    </div>
  )
}

function toolHelp(tool: keyof DeployTools) {
  switch (tool) {
    case 'docker': return 'Docker isn’t installed. Install Docker Desktop to build images.'
    case 'ssh': return 'The OpenSSH client isn’t installed (Windows: Settings → Apps → Optional features → OpenSSH Client).'
    case 'msDeploy': return 'Web Deploy isn’t installed on this computer. Install “Web Deploy 4” from Microsoft.'
    case 'azureCli': return 'The Azure CLI isn’t installed. Install it and run “az login” once.'
    default: return 'A required tool isn’t installed.'
  }
}
