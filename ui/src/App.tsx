import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import {
  Activity,
  ArrowDownLeft,
  ArrowUpRight,
  Check,
  ChevronDown,
  ChevronRight,
  CircleAlert,
  CircleCheck,
  Clock3,
  Command,
  Copy,
  CreditCard,
  Ellipsis,
  ExternalLink,
  Filter,
  LayoutDashboard,
  Plus,
  RefreshCw,
  Search,
  ShieldCheck,
  X,
  XCircle,
} from 'lucide-react'
import './App.css'

type PaymentStatus = 'Processing' | 'Succeeded' | 'Failed'
type StatusFilter = 'All payments' | 'Succeeded' | 'Processing' | 'Failed'

type PaymentAttempt = {
  number: number
  succeeded: boolean
  failureCode: string | null
  providerReference: string | null
  createdAt: string
}

type Payment = {
  id: string
  amountMinor: number
  currency: string
  provider: string
  providerReference: string | null
  status: PaymentStatus
  failureCode: string | null
  attemptCount: number
  createdAt: string
  updatedAt: string
  attempts: PaymentAttempt[]
}

type AuditEntry = {
  id: number
  event: string
  actor: string
  detail: string | null
  createdAt: string
}

const filters: StatusFilter[] = ['All payments', 'Succeeded', 'Processing', 'Failed']

function formatMoney(amountMinor: number, currency: string) {
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
    }).format(amountMinor / 100)
  } catch {
    return `${currency} ${(amountMinor / 100).toFixed(2)}`
  }
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(new Date(value))
}

function formatRelative(value: string, now: number) {
  const seconds = Math.max(0, Math.floor((now - new Date(value).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null
    throw new Error(body?.error || `Request failed (${response.status})`)
  }

  return response.json() as Promise<T>
}

function StatusBadge({ status }: { status: PaymentStatus }) {
  const Icon = status === 'Succeeded' ? CircleCheck : status === 'Processing' ? Clock3 : XCircle
  return (
    <span className={`status-badge status-${status.toLowerCase()}`}>
      <Icon size={14} strokeWidth={2.1} />
      {status}
    </span>
  )
}

function App() {
  const [payments, setPayments] = useState<Payment[]>([])
  const [providers, setProviders] = useState<string[]>(['sandbox'])
  const [filter, setFilter] = useState<StatusFilter>('All payments')
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState('')
  const [now, setNow] = useState(() => Date.now())
  const [lastUpdated, setLastUpdated] = useState(() => new Date())
  const [showCreate, setShowCreate] = useState(false)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedPayment, setSelectedPayment] = useState<Payment | null>(null)
  const [auditEntries, setAuditEntries] = useState<AuditEntry[]>([])
  const [detailError, setDetailError] = useState('')
  const [notice, setNotice] = useState('')
  const searchInput = useRef<HTMLInputElement>(null)

  const refresh = useCallback(async (quiet = false) => {
    if (quiet) setRefreshing(true)
    try {
      const [items, configuredProviders] = await Promise.all([
        request<Payment[]>('/api/payments?limit=100'),
        request<string[]>('/api/providers'),
      ])
      setPayments(items)
      setProviders(configuredProviders)
      const currentTime = Date.now()
      setNow(currentTime)
      setLastUpdated(new Date(currentTime))
      setError('')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load payments.')
    } finally {
      setLoading(false)
      setRefreshing(false)
    }
  }, [])

  useEffect(() => {
    let active = true
    Promise.all([
      request<Payment[]>('/api/payments?limit=100'),
      request<string[]>('/api/providers'),
    ])
      .then(([items, configuredProviders]) => {
        if (!active) return
        setPayments(items)
        setProviders(configuredProviders)
        const currentTime = Date.now()
        setNow(currentTime)
        setLastUpdated(new Date(currentTime))
        setError('')
      })
      .catch((cause: unknown) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Could not load payments.')
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
    const handleShortcut = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        searchInput.current?.focus()
      }
    }
    window.addEventListener('keydown', handleShortcut)
    return () => window.removeEventListener('keydown', handleShortcut)
  }, [])

  useEffect(() => {
    if (!selectedId) return
    let active = true
    Promise.all([
      request<Payment>(`/api/payments/${selectedId}`),
      request<AuditEntry[]>(`/api/payments/${selectedId}/audit`),
    ])
      .then(([payment, audit]) => {
        if (!active) return
        setSelectedPayment(payment)
        setAuditEntries(audit)
      })
      .catch((cause: unknown) => {
        if (active) setDetailError(cause instanceof Error ? cause.message : 'Could not load payment details.')
      })
    return () => { active = false }
  }, [selectedId, payments])

  useEffect(() => {
    if (!notice) return
    const timer = window.setTimeout(() => setNotice(''), 4000)
    return () => window.clearTimeout(timer)
  }, [notice])

  const visiblePayments = useMemo(() => {
    const query = search.trim().toLowerCase()
    return payments.filter((payment) => {
      const matchesStatus = filter === 'All payments' || payment.status === filter
      const matchesSearch = !query
        || payment.id.toLowerCase().includes(query)
        || payment.provider.toLowerCase().includes(query)
        || (payment.providerReference ?? '').toLowerCase().includes(query)
        || payment.status.toLowerCase().includes(query)
      return matchesStatus && matchesSearch
    })
  }, [filter, payments, search])

  const stats = useMemo(() => ({
    succeeded: payments.filter((payment) => payment.status === 'Succeeded').length,
    processing: payments.filter((payment) => payment.status === 'Processing').length,
    failed: payments.filter((payment) => payment.status === 'Failed').length,
    volumeByCurrency: payments
      .filter((payment) => payment.status === 'Succeeded')
      .reduce<Record<string, number>>((totals, payment) => {
        totals[payment.currency] = (totals[payment.currency] ?? 0) + payment.amountMinor
        return totals
      }, {}),
  }), [payments])
  const currencyVolumes = Object.entries(stats.volumeByCurrency)
  const volumeLabel = currencyVolumes.length > 1
    ? 'Multiple currencies'
    : formatMoney(currencyVolumes[0]?.[1] ?? 0, currencyVolumes[0]?.[0] ?? 'USD')

  const selectPayment = (id: string) => {
    if (id === selectedId) return
    setSelectedId(id)
    setSelectedPayment(null)
    setAuditEntries([])
    setDetailError('')
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="/" aria-label="Northstar Payments home">
          <span className="brand-mark"><Activity size={19} strokeWidth={2.4} /></span>
          <span className="brand-name">northstar<span>pay</span></span>
        </a>

        <div className="workspace-switcher">
          <span className="workspace-avatar">N</span>
          <span className="workspace-copy"><strong>Sandbox workspace</strong><small>Local environment</small></span>
          <ChevronDown size={15} />
        </div>

        <div className="nav-label">WORKSPACE</div>
        <nav className="main-nav" aria-label="Main navigation">
          <a className="nav-item" href="#overview"><LayoutDashboard size={17} /> Overview</a>
          <a className="nav-item active" href="#payments"><CreditCard size={17} /> Payments <span className="nav-count">{payments.length}</span></a>
        </nav>

        <div className="sidebar-bottom">
          <div className="environment-card">
            <span className="environment-icon"><ShieldCheck size={17} /></span>
            <span><strong>Sandbox mode</strong><small>Test transactions only</small></span>
            <span className="online-dot" />
          </div>
          <div className="profile">
            <div className="profile-avatar">JD</div>
            <div className="profile-copy"><strong>Local operator</strong><small>Sandbox access</small></div>
            <Ellipsis size={18} />
          </div>
        </div>
      </aside>

      <main className="main-content" id="payments">
        <header className="topbar">
          <div className="breadcrumbs"><span>Workspace</span><ChevronRight size={14} /><strong>Payments</strong></div>
          <div className="topbar-actions">
            <span className="environment-pill"><span /> Sandbox</span>
            <a className="docs-link" href="/swagger" target="_blank" rel="noreferrer">API docs <ExternalLink size={13} /></a>
            <button className={`icon-button ${refreshing ? 'is-spinning' : ''}`} onClick={() => void refresh(true)} aria-label="Refresh payments" title="Refresh">
              <RefreshCw size={17} />
            </button>
            <button className="avatar-button" aria-label="Account menu">JD</button>
          </div>
        </header>

        <div className="page-wrap">
          <section className="page-heading" id="overview">
            <div>
              <div className="eyebrow"><span className="eyebrow-line" /> PAYMENT OPERATIONS</div>
              <h1>Payments</h1>
              <p className="page-subtitle">Monitor transactions and manage your payment activity.</p>
            </div>
            <button className="primary-button" onClick={() => setShowCreate(true)}><Plus size={17} /> Create payment</button>
          </section>

          <section className="stats-grid" aria-label="Payment summary">
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Successful volume</span><span className="stat-icon mint"><ArrowDownLeft size={17} /></span></div>
              <div className={`stat-value ${currencyVolumes.length > 1 ? 'multi-currency' : ''}`}>{volumeLabel}</div>
              <div className="stat-foot"><span className="stat-dot green" />{currencyVolumes.length > 1 ? currencyVolumes.map(([currency, amount]) => `${formatMoney(amount, currency)} ${currency}`).join(' · ') : `From ${stats.succeeded} successful payments · recent 100`}</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Succeeded</span><span className="stat-icon blue"><CircleCheck size={17} /></span></div>
              <div className="stat-value">{stats.succeeded.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot green" /> Payments completed</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Processing</span><span className="stat-icon amber"><Clock3 size={17} /></span></div>
              <div className="stat-value">{stats.processing.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot amber-dot" /> Awaiting provider updates</div>
            </article>
            <article className="stat-card">
              <div className="stat-top"><span className="stat-label">Failed</span><span className="stat-icon rose"><CircleAlert size={17} /></span></div>
              <div className="stat-value">{stats.failed.toLocaleString()}</div>
              <div className="stat-foot"><span className="stat-dot red" /> Review failed attempts</div>
            </article>
          </section>

          <section className="transactions-card">
            <div className="table-card-heading">
              <div><h2>Recent transactions</h2><p>Search and inspect payment activity.</p></div>
              <button className="secondary-button export-button" onClick={() => downloadCsv(visiblePayments)}><ArrowUpRight size={15} /> Export CSV</button>
            </div>
            <div className="table-toolbar">
              <div className="filter-tabs" role="tablist" aria-label="Filter by payment status">
                {filters.map((item) => (
                  <button
                    className={`filter-tab ${filter === item ? 'selected' : ''}`}
                    key={item}
                    onClick={() => setFilter(item)}
                    role="tab"
                    aria-selected={filter === item}
                  >
                    {item}
                    {item === 'All payments' && <span className="tab-count">{payments.length}</span>}
                  </button>
                ))}
              </div>
              <div className="table-actions">
                <label className="search-field">
                  <Search size={16} />
                  <input ref={searchInput} aria-label="Search payments" placeholder="Search payments..." value={search} onChange={(event) => setSearch(event.target.value)} />
                  <kbd><Command size={11} /> K</kbd>
                </label>
                <button className="filter-button" onClick={() => setFilter('All payments')} aria-label="Clear status filters" title="Clear status filter"><Filter size={16} /></button>
              </div>
            </div>

            {error && <div className="inline-error"><CircleAlert size={16} /><span>{error}</span><button onClick={() => { setLoading(true); void refresh(true) }}><RefreshCw size={14} /> Retry</button></div>}

            <div className="table-scroll">
              <table className="payments-table">
                <thead><tr>
                  <th><span className="th-check"><input type="checkbox" aria-label="Select all payments" disabled /></span></th>
                  <th>PAYMENT</th><th>STATUS</th><th>AMOUNT</th><th>PROVIDER</th><th>CREATED</th><th>ATTEMPTS</th><th />
                </tr></thead>
                <tbody>
                  {loading ? (
                    <tr><td colSpan={8}><div className="loading-state"><span className="spinner" /> Loading payments…</div></td></tr>
                  ) : visiblePayments.length === 0 ? (
                    <tr><td colSpan={8}>
                      <div className="empty-state">
                        <span className="empty-icon"><CreditCard size={22} /></span>
                        <strong>{payments.length ? 'No matching payments' : 'No payments yet'}</strong>
                        <span>{payments.length ? 'Try another search or status filter.' : 'Create your first sandbox payment to see it here.'}</span>
                        {!payments.length && <button className="secondary-button" onClick={() => setShowCreate(true)}><Plus size={15} /> Create payment</button>}
                      </div>
                    </td></tr>
                  ) : visiblePayments.map((payment) => (
                    <tr className={`payment-row ${selectedId === payment.id ? 'row-selected' : ''}`} key={payment.id} onClick={() => selectPayment(payment.id)} tabIndex={0} onKeyDown={(event) => { if (event.key === 'Enter') selectPayment(payment.id) }}>
                      <td><span className="row-check"><input type="checkbox" aria-label={`Select payment ${payment.id}`} onClick={(event) => event.stopPropagation()} /></span></td>
                      <td><div className="payment-cell"><span className={`payment-method-icon method-${payment.status.toLowerCase()}`}><CreditCard size={16} /></span><span className="payment-id-wrap"><strong>{shortId(payment.id)}</strong><small>{payment.providerReference ? `Ref ${payment.providerReference}` : `ID ${payment.id.slice(0, 12)}`}</small></span></div></td>
                      <td><StatusBadge status={payment.status} /></td>
                      <td><span className="amount-cell">{formatMoney(payment.amountMinor, payment.currency)}</span></td>
                      <td><span className="provider-cell"><span className="provider-logo">{payment.provider.slice(0, 1).toUpperCase()}</span>{payment.provider}</span></td>
                      <td><span className="date-cell" title={formatDate(payment.createdAt)}>{formatRelative(payment.createdAt, now)}</span></td>
                      <td><span className={`attempts-cell ${payment.attemptCount > 1 ? 'has-retries' : ''}`}>{payment.attemptCount} {payment.attemptCount === 1 ? 'attempt' : 'attempts'}</span></td>
                      <td><button className="row-more" aria-label={`View ${payment.id} details`} onClick={(event) => { event.stopPropagation(); selectPayment(payment.id) }}><ChevronRight size={16} /></button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="table-footer">
              <span>Showing <strong>{visiblePayments.length}</strong> of <strong>{payments.length}</strong> recent transactions</span>
              <span className="updated-note"><span className="live-dot" /> Updated {formatRelative(lastUpdated.toISOString(), now)}</span>
            </div>
          </section>
          <footer className="page-footer"><span>© 2026 Northstar Pay</span><span><ShieldCheck size={13} /> Token-only sandbox environment</span></footer>
        </div>
      </main>

      {showCreate && <CreatePaymentDialog providers={providers} onClose={() => setShowCreate(false)} onCreated={async (payment) => {
        setShowCreate(false)
        setNotice(`Payment ${shortId(payment.id)} ${payment.status.toLowerCase()}.`)
        await refresh(true)
        selectPayment(payment.id)
      }} />}

      {selectedId && (
        <div className="drawer-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) setSelectedId(null) }}>
          <aside className="detail-drawer" aria-label="Payment details">
            <div className="drawer-header"><div><span className="drawer-kicker">TRANSACTION DETAILS</span><h2>Payment</h2></div><button className="icon-button" onClick={() => setSelectedId(null)} aria-label="Close details"><X size={18} /></button></div>
            {detailError ? <div className="drawer-loading detail-error">{detailError}</div> : !selectedPayment ? <div className="drawer-loading"><span className="spinner" /> Loading details…</div> : (
              <>
                <div className="drawer-amount">{formatMoney(selectedPayment.amountMinor, selectedPayment.currency)}<StatusBadge status={selectedPayment.status} /></div>
                <div className="detail-id"><span>{selectedPayment.id}</span><CopyButton value={selectedPayment.id} /></div>
                <div className="detail-grid">
                  <Detail label="Provider" value={selectedPayment.provider} />
                  <Detail label="Created" value={formatDate(selectedPayment.createdAt)} />
                  <Detail label="Provider reference" value={selectedPayment.providerReference ?? '—'} />
                  <Detail label="Attempts" value={String(selectedPayment.attemptCount)} />
                  {selectedPayment.failureCode && <Detail label="Failure code" value={selectedPayment.failureCode} danger />}
                </div>
                <div className="drawer-section"><div className="section-title"><h3>Attempt history</h3><span>{selectedPayment.attempts.length}</span></div>
                  {selectedPayment.attempts.map((attempt) => <div className="attempt-item" key={attempt.number}><span className={`timeline-icon ${attempt.succeeded ? 'success' : attempt.failureCode ? 'failure' : 'pending'}`}>{attempt.succeeded ? <Check size={13} /> : attempt.failureCode ? <X size={13} /> : <Clock3 size={13} />}</span><div><strong>Attempt {attempt.number} · {attempt.succeeded ? 'Succeeded' : attempt.failureCode ?? 'Processing'}</strong><small>{formatDate(attempt.createdAt)}</small></div></div>)}
                </div>
                <div className="drawer-section audit-section"><div className="section-title"><h3>Audit trail</h3><span>{auditEntries.length}</span></div>
                  {auditEntries.map((entry) => <div className="audit-item" key={entry.id}><span className="audit-line" /><div><strong>{humanize(entry.event)}</strong><small>{entry.actor}{entry.detail ? ` · ${entry.detail}` : ''}</small><small>{formatDate(entry.createdAt)}</small></div></div>)}
                  {!auditEntries.length && <div className="muted-copy">No audit events recorded.</div>}
                </div>
              </>
            )}
            <div className="drawer-footer"><button className="secondary-button" onClick={() => void refresh(true)}><RefreshCw size={15} /> Refresh status</button><a href="/swagger" target="_blank" rel="noreferrer">View API docs <ExternalLink size={13} /></a></div>
          </aside>
        </div>
      )}

      {notice && <div className="toast"><span><Check size={15} /></span>{notice}<button onClick={() => setNotice('')} aria-label="Dismiss notification"><X size={15} /></button></div>}
    </div>
  )
}

function CreatePaymentDialog({ providers, onClose, onCreated }: { providers: string[]; onClose: () => void; onCreated: (payment: Payment) => Promise<void> }) {
  const [amount, setAmount] = useState('')
  const [currency, setCurrency] = useState('USD')
  const [provider, setProvider] = useState('sandbox')
  const [paymentToken, setPaymentToken] = useState('')
  const [idempotencyKey] = useState(() => crypto.randomUUID())
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError('')
    if (!/^\d+(\.\d{1,2})?$/.test(amount) || Number(amount) <= 0) {
      setError('Enter an amount with up to two decimal places.')
      return
    }
    const [whole, fraction = ''] = amount.split('.')
    const amountMinor = Number(whole) * 100 + Number(fraction.padEnd(2, '0'))
    setSubmitting(true)
    try {
      const payment = await request<Payment>('/api/payments/', {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify({ amountMinor, currency, provider, paymentToken }),
      })
      setPaymentToken('')
      await onCreated(payment)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Payment could not be created.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget && !submitting) onClose() }}>
      <section className="create-dialog" role="dialog" aria-modal="true" aria-labelledby="create-title">
        <div className="dialog-heading"><div><span className="drawer-kicker">SANDBOX TRANSACTION</span><h2 id="create-title">Create payment</h2><p>Initiate a tokenized payment through a configured provider.</p></div><button className="icon-button" onClick={onClose} disabled={submitting} aria-label="Close"><X size={18} /></button></div>
        <form onSubmit={submit}>
          <label className="form-label" htmlFor="payment-amount">Amount</label>
          <div className="amount-input-row"><span>$</span><input id="payment-amount" autoFocus inputMode="decimal" placeholder="0.00" value={amount} onChange={(event) => setAmount(event.target.value)} required /><select value={currency} onChange={(event) => setCurrency(event.target.value)} aria-label="Currency"><option>USD</option><option>EUR</option><option>GBP</option><option>CAD</option><option>AUD</option></select></div>
          <label className="form-label" htmlFor="provider">Payment provider</label>
          <div className="select-wrap"><select id="provider" value={provider} onChange={(event) => setProvider(event.target.value)}>{providers.map((name) => <option key={name} value={name}>{name === 'sandbox' ? 'Sandbox simulator' : `${name} sandbox gateway`}</option>)}</select><ChevronDown size={15} /></div>
          <label className="form-label" htmlFor="payment-token">Provider payment token</label>
          <input className="text-input token-input" id="payment-token" autoComplete="off" placeholder="e.g. sandbox_retry" value={paymentToken} onChange={(event) => setPaymentToken(event.target.value)} required />
          <p className="field-hint"><ShieldCheck size={14} /> Use a provider-issued token. Never enter card numbers or CVV.</p>
          <div className="token-examples"><span>Simulator examples</span><button type="button" onClick={() => setPaymentToken('sandbox_retry')}>Retry → succeed</button><button type="button" onClick={() => setPaymentToken('sandbox_decline')}>Decline</button><button type="button" onClick={() => setPaymentToken('sandbox_failure')}>Exhaust retries</button></div>
          <div className="idempotency-note"><ShieldCheck size={15} /><span><strong>Duplicate-safe request</strong><small>An idempotency key is generated and reused if you retry this submission.</small></span></div>
          {error && <div className="form-error"><CircleAlert size={15} />{error}</div>}
          <div className="dialog-actions"><button type="button" className="secondary-button" onClick={onClose} disabled={submitting}>Cancel</button><button className="primary-button" type="submit" disabled={submitting}>{submitting ? <><span className="button-spinner" /> Processing…</> : <><Plus size={16} /> Create payment</>}</button></div>
        </form>
      </section>
    </div>
  )
}

function Detail({ label, value, danger = false }: { label: string; value: string; danger?: boolean }) {
  return <div className="detail-cell"><span>{label}</span><strong className={danger ? 'danger-text' : ''}>{value}</strong></div>
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)
  return <button className="copy-button" aria-label="Copy payment ID" title="Copy ID" onClick={() => {
    void navigator.clipboard.writeText(value).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    })
  }}>{copied ? <Check size={13} /> : <Copy size={13} />}</button>
}

function shortId(id: string) {
  return `pay_${id.slice(0, 8)}`
}

function humanize(value: string) {
  return value.split('_').map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(' ')
}

function downloadCsv(payments: Payment[]) {
  const columns = ['id', 'status', 'amountMinor', 'currency', 'provider', 'providerReference', 'attemptCount', 'createdAt']
  const rows = payments.map((payment) => [
    payment.id,
    payment.status,
    payment.amountMinor,
    payment.currency,
    payment.provider,
    payment.providerReference ?? '',
    payment.attemptCount,
    payment.createdAt,
  ])
  const csv = [columns, ...rows].map((row) => row.map((value) => `"${String(value).replaceAll('"', '""')}"`).join(',')).join('\n')
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'payments.csv'
  link.click()
  URL.revokeObjectURL(url)
}

export default App
