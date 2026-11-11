import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  Activity,
  ArrowDownLeft,
  Check,
  ChevronRight,
  CircleAlert,
  CircleCheck,
  Clock3,
  Database,
  Download,
  ExternalLink,
  FileSpreadsheet,
  Plus,
  RefreshCw,
  RotateCcw,
  ShieldCheck,
  Upload,
  X,
} from 'lucide-react'
import './Reconciliation.css'

type BatchStatus = 'Pending' | 'Processing' | 'Completed' | 'Failed'
type FindingStatus = 'Pending' | 'Matched' | 'Duplicate' | 'Mismatched' | 'MissingInternal' | 'MissingSettlement'

type Batch = {
  id: string
  fileName: string
  status: BatchStatus
  createdAt: string
  processedAt: string | null
  errorMessage: string | null
  summary: {
    total: number
    matched: number
    duplicate: number
    mismatched: number
    missingInternal: number
    missingSettlement: number
  }
}

type BatchDetails = {
  batch: Batch
  items: Array<{
    rowNumber: number
    source: string
    transactionReference: string
    amountMinor: number
    currency: string
    settledAt: string
    status: FindingStatus
    internalTransactionId: string | null
    detail: string | null
  }>
  auditEntries: Array<{
    id: number
    event: string
    actor: string
    detail: string | null
    createdAt: string
  }>
}

type InternalTransaction = {
  id: string
  externalReference: string
  amountMinor: number
  currency: string
  occurredAt: string
  createdAt: string
}

async function apiRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const isForm = init?.body instanceof FormData
  const response = await fetch(path, {
    ...init,
    headers: {
      ...(!isForm && init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string; detail?: string; title?: string } | null
    throw new Error(body?.error || body?.detail || body?.title || `Request failed (${response.status})`)
  }
  return response.json() as Promise<T>
}

function money(amountMinor: number, currency: string) {
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(amountMinor / 100)
  } catch {
    return `${currency} ${(amountMinor / 100).toFixed(2)}`
  }
}

function dateLabel(value: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(value))
}

function StatusTag({ status }: { status: BatchStatus | FindingStatus }) {
  const icon = status === 'Matched' || status === 'Completed'
    ? <CircleCheck size={13} />
    : status === 'Pending' || status === 'Processing'
      ? <Clock3 size={13} />
      : <CircleAlert size={13} />
  const label = status === 'MissingInternal'
    ? 'Missing internal'
    : status === 'MissingSettlement'
      ? 'Missing settlement'
      : status
  return <span className={`recon-status recon-${status.toLowerCase()}`}>{icon}{label}</span>
}

export default function ReconciliationApp({ onBack }: { onBack: () => void }) {
  const [batches, setBatches] = useState<Batch[]>([])
  const [transactions, setTransactions] = useState<InternalTransaction[]>([])
  const [details, setDetails] = useState<BatchDetails | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [reference, setReference] = useState('')
  const [amount, setAmount] = useState('')
  const [currency, setCurrency] = useState('USD')
  const [occurredAt, setOccurredAt] = useState(() => {
    const now = new Date()
    now.setMinutes(now.getMinutes() - now.getTimezoneOffset())
    return now.toISOString().slice(0, 16)
  })

  const refresh = useCallback(async (quiet = false) => {
    if (quiet) setRefreshing(true)
    try {
      const [batchList, transactionList] = await Promise.all([
        apiRequest<Batch[]>('/api/reconciliation/batches?limit=50'),
        apiRequest<InternalTransaction[]>('/api/reconciliation/transactions?limit=100'),
      ])
      setBatches(batchList)
      setTransactions(transactionList)
      setError('')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load reconciliation data.')
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    Promise.all([
      apiRequest<Batch[]>('/api/reconciliation/batches?limit=50'),
      apiRequest<InternalTransaction[]>('/api/reconciliation/transactions?limit=100'),
    ])
      .then(([batchList, transactionList]) => {
        if (!active) return
        setBatches(batchList)
        setTransactions(transactionList)
        setError('')
      })
      .catch((cause: unknown) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Could not load reconciliation data.')
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    const timer = window.setInterval(() => void refresh(true), 15_000)
    return () => {
      active = false
      window.clearInterval(timer)
    }
  }, [refresh])

  useEffect(() => {
    if (!message) return
    const timer = window.setTimeout(() => setMessage(''), 4000)
    return () => window.clearTimeout(timer)
  }, [message])

  const metrics = useMemo(() => {
    const completed = batches.filter((batch) => batch.status === 'Completed')
    return {
      matched: completed.reduce((total, batch) => total + batch.summary.matched, 0),
      exceptions: completed.reduce((total, batch) => total
        + batch.summary.duplicate
        + batch.summary.mismatched
        + batch.summary.missingInternal
        + batch.summary.missingSettlement, 0),
      processing: batches.filter((batch) => batch.status === 'Pending' || batch.status === 'Processing').length,
    }
  }, [batches])

  const openBatch = async (id: string) => {
    setError('')
    try {
      setDetails(await apiRequest<BatchDetails>(`/api/reconciliation/reports/${id}`))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load the reconciliation report.')
    }
  }

  const upload = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const formElement = event.currentTarget
    if (!file) return
    setBusy(true)
    setError('')
    try {
      const form = new FormData()
      form.append('file', file)
      const batch = await apiRequest<Batch>('/api/reconciliation/batches', { method: 'POST', body: form })
      setFile(null)
      formElement.reset()
      setMessage(`Imported ${batch.fileName}; reconciliation is queued.`)
      await refresh(true)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The settlement file could not be imported.')
      await refresh(true)
    } finally {
      setBusy(false)
    }
  }

  const createTransaction = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!/^\d+(\.\d{1,2})?$/.test(amount) || Number(amount) <= 0) {
      setError('Enter an amount with up to two decimal places.')
      return
    }
    const [whole, fraction = ''] = amount.split('.')
    const amountMinor = Number(whole) * 100 + Number(fraction.padEnd(2, '0'))
    setBusy(true)
    setError('')
    try {
      await apiRequest<InternalTransaction>('/api/reconciliation/transactions', {
        method: 'POST',
        body: JSON.stringify({
          externalReference: reference,
          amountMinor,
          currency,
          occurredAt: new Date(occurredAt).toISOString(),
        }),
      })
      setReference('')
      setAmount('')
      setMessage(`Internal transaction ${reference} added.`)
      await refresh(true)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The internal transaction could not be created.')
    } finally {
      setBusy(false)
    }
  }

  const requeue = async (batch: Batch) => {
    setBusy(true)
    setError('')
    try {
      await apiRequest<Batch>(`/api/reconciliation/batches/${batch.id}/requeue`, { method: 'POST' })
      setMessage(`Reconciliation queued for ${batch.fileName}.`)
      await refresh(true)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The batch could not be requeued.')
    } finally {
      setBusy(false)
    }
  }

  const downloadTemplate = () => {
    const template = 'transactionReference,amount,currency,settledAt\nPAY-1001,125.50,USD,2026-11-12T10:30:00Z\n'
    const link = document.createElement('a')
    link.href = URL.createObjectURL(new Blob([template], { type: 'text/csv;charset=utf-8' }))
    link.download = 'settlement-template.csv'
    link.click()
    URL.revokeObjectURL(link.href)
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="#reconciliation" aria-label="Settlement operations home">
          <span className="brand-mark"><Activity size={19} strokeWidth={2.4} /></span>
          <span className="brand-name">northstar<span>pay</span></span>
        </a>
        <div className="workspace-switcher">
          <span className="workspace-avatar">N</span>
          <span className="workspace-copy"><strong>Sandbox workspace</strong><small>Local environment</small></span>
        </div>
        <div className="nav-label">WORKSPACE</div>
        <nav className="main-nav" aria-label="Main navigation">
          <button className="nav-item recon-nav-active"><ArrowDownLeft size={17} /> Reconciliation</button>
          <button className="nav-item" onClick={onBack}><Database size={17} /> Payments</button>
        </nav>
        <div className="sidebar-bottom">
          <div className="environment-card">
            <span className="environment-icon"><ShieldCheck size={17} /></span>
            <span><strong>Settlement operations</strong><small>Local environment</small></span>
            <span className="online-dot" />
          </div>
          <div className="profile">
            <div className="profile-avatar">JD</div>
            <div className="profile-copy"><strong>Local operator</strong><small>Reconciliation access</small></div>
          </div>
        </div>
      </aside>

      <main className="main-content" id="reconciliation">
        <header className="topbar">
          <div className="breadcrumbs"><span>Workspace</span><ChevronRight size={14} /><strong>Reconciliation</strong></div>
          <div className="topbar-actions">
            <span className="environment-pill"><span /> Sandbox</span>
            <a className="docs-link" href="/swagger" target="_blank" rel="noreferrer">API docs <ExternalLink size={13} /></a>
            <button className={`icon-button ${refreshing ? 'is-spinning' : ''}`} onClick={() => void refresh(true)} aria-label="Refresh reconciliation data"><RefreshCw size={17} /></button>
          </div>
        </header>

        <div className="page-wrap">
          <section className="page-heading">
            <div>
              <div className="eyebrow"><span className="eyebrow-line" /> BANK SETTLEMENT OPERATIONS</div>
              <h1>Reconciliation</h1>
              <p className="page-subtitle">Compare bank settlement records with internal payment transactions.</p>
            </div>
            <button className="secondary-button recon-template" onClick={downloadTemplate}><Download size={15} /> CSV template</button>
          </section>

          <section className="stats-grid" aria-label="Reconciliation summary">
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Matched records</span><span className="stat-icon mint"><CircleCheck size={17} /></span></div>
              <div className="stat-value">{metrics.matched.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot green" /> Across completed batches</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Exceptions</span><span className="stat-icon rose"><CircleAlert size={17} /></span></div>
              <div className="stat-value">{metrics.exceptions.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot red" /> Require operations review</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Settlement batches</span><span className="stat-icon blue"><FileSpreadsheet size={17} /></span></div>
              <div className="stat-value">{batches.length.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot green" /> Imported reconciliation files</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Internal transactions</span><span className="stat-icon amber"><Database size={17} /></span></div>
              <div className="stat-value">{transactions.length.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot amber-dot" /> {metrics.processing} batches in queue</div>
            </article>
          </section>

          {error && <div className="inline-error"><CircleAlert size={16} /><span>{error}</span><button onClick={() => void refresh(true)}><RefreshCw size={14} /> Retry</button></div>}

          <section className="recon-workspace">
            <article className="recon-panel import-panel">
              <div className="recon-panel-heading">
                <span className="recon-panel-icon"><Upload size={17} /></span>
                <div><h2>Import bank settlement</h2><p>Upload a CSV file to queue a reconciliation run.</p></div>
              </div>
              <form onSubmit={upload}>
                <label className="recon-file-picker" htmlFor="settlement-file">
                  <FileSpreadsheet size={21} />
                  <strong>{file?.name ?? 'Choose a settlement CSV'}</strong>
                  <span>{file ? `${(file.size / 1024).toFixed(1)} KB · Ready to upload` : 'CSV format · up to 5 MB'}</span>
                  <input id="settlement-file" type="file" accept=".csv,text/csv" onChange={(event) => setFile(event.target.files?.[0] ?? null)} />
                </label>
                <div className="recon-format-note"><strong>Required columns</strong><code>transactionReference, amount, currency, settledAt</code></div>
                <button className="primary-button recon-submit" type="submit" disabled={!file || busy}>
                  {busy ? <><span className="button-spinner" /> Working…</> : <><Upload size={15} /> Import and reconcile</>}
                </button>
              </form>
            </article>

            <article className="recon-panel">
              <div className="recon-panel-heading">
                <span className="recon-panel-icon recon-blue"><Plus size={17} /></span>
                <div><h2>Add internal transaction</h2><p>Record an internal payment for comparison against bank files.</p></div>
              </div>
              <form className="internal-form" onSubmit={createTransaction}>
                <label>Transaction reference<input value={reference} onChange={(event) => setReference(event.target.value)} maxLength={200} required placeholder="e.g. PAY-1001" /></label>
                <div className="recon-form-row">
                  <label>Amount<input value={amount} onChange={(event) => setAmount(event.target.value)} inputMode="decimal" required placeholder="0.00" /></label>
                  <label>Currency<select value={currency} onChange={(event) => setCurrency(event.target.value)}><option>USD</option><option>EUR</option><option>GBP</option><option>CAD</option><option>AUD</option></select></label>
                </div>
                <label>Transaction date<input type="datetime-local" value={occurredAt} onChange={(event) => setOccurredAt(event.target.value)} required /></label>
                <button className="primary-button recon-submit" type="submit" disabled={busy}><Plus size={15} /> Add transaction</button>
              </form>
            </article>
          </section>

          {message && <div className="recon-toast"><span><Check size={14} /></span>{message}<button onClick={() => setMessage('')} aria-label="Dismiss notification"><X size={14} /></button></div>}

          <section className="transactions-card recon-batches">
            <div className="table-card-heading">
              <div><h2>Settlement batches</h2><p>Review processing status, reconciliation outcomes, and audit history.</p></div>
              <button className="secondary-button export-button" onClick={() => void refresh(true)} disabled={refreshing}><RefreshCw size={14} /> Refresh</button>
            </div>
            <div className="table-scroll">
              <table className="payments-table recon-table">
                <thead><tr><th>SETTLEMENT FILE</th><th>STATUS</th><th>RECORDS</th><th>MATCHED</th><th>EXCEPTIONS</th><th>IMPORTED</th><th /></tr></thead>
                <tbody>
                  {loading ? (
                    <tr><td colSpan={7}><div className="loading-state"><span className="spinner" /> Loading batches…</div></td></tr>
                  ) : batches.length === 0 ? (
                    <tr><td colSpan={7}><div className="recon-empty"><FileSpreadsheet size={24} /><strong>No settlement batches yet</strong><span>Import a bank settlement CSV to start your first reconciliation.</span></div></td></tr>
                  ) : batches.map((batch) => {
                    const exceptions = batch.summary.duplicate + batch.summary.mismatched + batch.summary.missingInternal + batch.summary.missingSettlement
                    return (
                      <tr className="recon-batch-row" key={batch.id} onClick={() => void openBatch(batch.id)}>
                        <td><div className="recon-file-cell"><span><FileSpreadsheet size={15} /></span><div><strong>{batch.fileName}</strong><small>{batch.id.slice(0, 12)}</small></div></div></td>
                        <td><StatusTag status={batch.status} />{(batch.status === 'Failed' || batch.status === 'Pending') && <button className="recon-retry" onClick={(event) => { event.stopPropagation(); void requeue(batch) }} disabled={busy} title="Retry batch"><RotateCcw size={14} /></button>}</td>
                        <td>{batch.summary.total.toLocaleString()}</td>
                        <td><span className="recon-count-good">{batch.summary.matched.toLocaleString()}</span></td>
                        <td><span className={exceptions ? 'recon-count-bad' : ''}>{exceptions.toLocaleString()}</span></td>
                        <td><span className="date-cell">{dateLabel(batch.createdAt)}</span></td>
                        <td><button className="row-more" aria-label={`View ${batch.fileName} report`} onClick={(event) => { event.stopPropagation(); void openBatch(batch.id) }}><ChevronRight size={16} /></button></td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            <div className="table-footer">
              <span>Showing <strong>{batches.length}</strong> recent batches</span>
              <span className="updated-note"><span className="live-dot" /> Auto-refresh every 15 seconds</span>
            </div>
          </section>
          <footer className="page-footer"><span>© 2026 Northstar Pay</span><span><ShieldCheck size={13} /> Settlement reconciliation workspace</span></footer>
        </div>
      </main>

      {details && (
        <div className="drawer-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) setDetails(null) }}>
          <aside className="detail-drawer recon-drawer" aria-label="Reconciliation report">
            <div className="drawer-header"><div><span className="drawer-kicker">RECONCILIATION REPORT</span><h2>{details.batch.fileName}</h2></div><button className="icon-button" onClick={() => setDetails(null)} aria-label="Close report"><X size={18} /></button></div>
            <div className="recon-report-status"><StatusTag status={details.batch.status} /><span>{dateLabel(details.batch.createdAt)}</span></div>
            {details.batch.errorMessage && <div className="form-error"><CircleAlert size={15} />{details.batch.errorMessage}</div>}
            <div className="recon-summary">
              <div><strong>{details.batch.summary.matched}</strong><span>Matched</span></div>
              <div><strong>{details.batch.summary.duplicate}</strong><span>Duplicate</span></div>
              <div><strong>{details.batch.summary.mismatched}</strong><span>Mismatched</span></div>
              <div><strong>{details.batch.summary.missingInternal}</strong><span>Missing internal</span></div>
              <div><strong>{details.batch.summary.missingSettlement}</strong><span>Missing settlement</span></div>
            </div>
            <div className="drawer-section">
              <div className="section-title"><h3>Findings</h3><span>{details.items.length}</span></div>
              {details.items.map((item) => (
                <div className="finding-item" key={`${item.source}-${item.rowNumber}`}>
                  <div className="finding-top"><strong>{item.transactionReference}</strong><StatusTag status={item.status} /></div>
                  <div className="finding-meta">{money(item.amountMinor, item.currency)} · {item.source} · {dateLabel(item.settledAt)}</div>
                  {item.detail && <div className="finding-detail">{item.detail}</div>}
                </div>
              ))}
              {!details.items.length && <div className="muted-copy">No findings available yet.</div>}
            </div>
            <div className="drawer-section audit-section">
              <div className="section-title"><h3>Audit trail</h3><span>{details.auditEntries.length}</span></div>
              {details.auditEntries.map((entry) => (
                <div className="audit-item" key={entry.id}><span className="audit-line" /><div><strong>{entry.event.replaceAll('_', ' ')}</strong><small>{entry.actor}{entry.detail ? ` · ${entry.detail}` : ''}</small><small>{dateLabel(entry.createdAt)}</small></div></div>
              ))}
            </div>
            <div className="drawer-footer"><a href="/swagger" target="_blank" rel="noreferrer">View API docs <ExternalLink size={13} /></a></div>
          </aside>
        </div>
      )}
    </div>
  )
}
