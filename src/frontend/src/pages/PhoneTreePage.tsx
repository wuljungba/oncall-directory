import { useState, useEffect } from 'react'
import {
  PhoneCall, ChevronRight, AlertTriangle, Plus, Save, X, Trash2, Edit3, ArrowUp, ArrowDown,
} from 'lucide-react'
import { phoneTreesApi } from '@/services/api'
import type { PhoneTree, PhoneTreeNode } from '@/types'

export default function PhoneTreePage() {
  const [trees, setTrees] = useState<PhoneTree[]>([])
  const [selectedTree, setSelectedTree] = useState<PhoneTree | null>(null)
  const [loading, setLoading] = useState(true)

  // Modal states
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [showEditModal, setShowEditModal] = useState(false)
  const [showAddNodeModal, setShowAddNodeModal] = useState(false)
  const [deleting, setDeleting] = useState(false)

  useEffect(() => {
    loadTrees()
  }, [])

  async function loadTrees() {
    try {
      const data = await phoneTreesApi.getAll()
      setTrees(data)
    } catch { /* ignore */ }
    setLoading(false)
  }

  function refreshSelectedTree() {
    if (!selectedTree) return
    phoneTreesApi.get(selectedTree.id).then((updated) => {
      setSelectedTree(updated)
      setTrees((prev) => prev.map((t) => (t.id === updated.id ? updated : t)))
    })
  }

  const codeTypeColor = (type: string) => {
    switch (type) {
      case 'code-blue': return 'bg-blue-600/20 text-blue-500 border-blue-600/30'
      case 'code-red': return 'bg-red-600/20 text-red-500 border-red-600/30'
      case 'code-green': return 'bg-green-600/20 text-green-500 border-green-600/30'
      case 'code-silver': return 'bg-gray-400/20 text-gray-300 border-gray-400/30'
      case 'code-grey': return 'bg-slate-600/20 text-slate-400 border-slate-600/30'
      case 'code-pink': return 'bg-pink-600/20 text-pink-400 border-pink-600/30'
      case 'emergency': return 'bg-red-600/20 text-red-500'
      case 'department': return 'bg-blue-600/20 text-blue-500'
      case 'oncall': return 'bg-amber-600/20 text-amber-500'
      case 'admin': return 'bg-purple-600/20 text-purple-500'
      default: return 'bg-gray-600/20 text-gray-500'
    }
  }

  const codeTypeLabel = (type: string) => {
    const labels: Record<string, string> = {
      'code-blue': 'Code Blue — Cardiac Arrest',
      'code-red': 'Code Red — Fire',
      'code-green': 'Code Green — Evacuation',
      'code-silver': 'Code Silver — Active Threat',
      'code-grey': 'Code Grey — Severe Weather',
      'code-pink': 'Code Pink — Infant Abduction',
      'emergency': 'Emergency',
      'department': 'Department',
      'oncall': 'On-Call',
      'admin': 'Admin',
    }
    return labels[type] || type
  }

  async function handleCreate(data: Partial<PhoneTree>) {
    const created = await phoneTreesApi.create(data)
    setTrees((prev) => [...prev, created])
    setSelectedTree(created)
    setShowCreateModal(false)
  }

  async function handleUpdate(data: Partial<PhoneTree>) {
    if (!selectedTree) return
    const updated = await phoneTreesApi.update(selectedTree.id, data)
    setSelectedTree(updated)
    setTrees((prev) => prev.map((t) => (t.id === updated.id ? updated : t)))
    setShowEditModal(false)
  }

  async function handleDelete(id: number) {
    setDeleting(true)
    try {
      await phoneTreesApi.delete(id)
      setTrees((prev) => prev.filter((t) => t.id !== id))
      if (selectedTree?.id === id) setSelectedTree(null)
    } finally {
      setDeleting(false)
    }
  }

  async function handleAddNode(data: Partial<PhoneTreeNode>) {
    if (!selectedTree) return
    await phoneTreesApi.addNode(selectedTree.id, data)
    refreshSelectedTree()
    setShowAddNodeModal(false)
  }

  async function handleRemoveNode(nodeId: number) {
    await phoneTreesApi.removeNode(nodeId)
    refreshSelectedTree()
  }

  async function handleMoveNode(nodeId: number, direction: 'up' | 'down') {
    if (!selectedTree) return
    const sorted = [...selectedTree.nodes].sort((a, b) => a.order - b.order)
    const idx = sorted.findIndex((n) => n.id === nodeId)
    if (idx === -1) return
    if (direction === 'up' && idx === 0) return
    if (direction === 'down' && idx === sorted.length - 1) return

    const swapIdx = direction === 'up' ? idx - 1 : idx + 1;
    [sorted[idx].order, sorted[swapIdx].order] = [sorted[swapIdx].order, sorted[idx].order]

    await phoneTreesApi.reorder(selectedTree.id, sorted.map((n) => n.id))
    refreshSelectedTree()
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Emergency Code Configuration</h1>
        <button
          onClick={() => setShowCreateModal(true)}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
        >
          <Plus className="w-4 h-4" />
          New Code
        </button>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Tree List */}
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800">
            <p className="text-sm text-gray-500">{trees.length} code{trees.length !== 1 ? 's' : ''}</p>
          </div>
          <div className="divide-y divide-gray-800 max-h-[500px] overflow-y-auto">
            {loading ? (
              <div className="flex items-center justify-center py-12">
                <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" />
              </div>
            ) : trees.length === 0 ? (
              <div className="text-center py-12 text-gray-500">
                <PhoneCall className="w-8 h-8 mx-auto mb-2 text-gray-700" />
                <p className="text-sm">No emergency codes configured</p>
              </div>
            ) : (
              trees.map((tree) => (
                <button
                  key={tree.id}
                  onClick={() => setSelectedTree(tree)}
                  className={`w-full text-left px-5 py-4 hover:bg-gray-800/50 transition-colors ${
                    selectedTree?.id === tree.id ? 'bg-gray-800' : ''
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm font-medium">{tree.name}</p>
                      <span
                        className={`text-xs px-2 py-0.5 rounded-full mt-1 inline-block ${codeTypeColor(tree.treeType)}`}
                      >
                        {codeTypeLabel(tree.treeType)}
                      </span>
                    </div>
                    <ChevronRight className="w-4 h-4 text-gray-600" />
                  </div>
                </button>
              ))
            )}
          </div>
        </div>

        {/* Tree Detail & Management */}
        <div className="lg:col-span-2 bg-gray-900 border border-gray-800 rounded-xl p-5">
          {selectedTree ? (
            <div className="space-y-5">
              <div className="flex items-start justify-between">
                <div>
                  <h2 className="text-lg font-medium">{selectedTree.name}</h2>
                  <span
                    className={`text-xs px-2 py-0.5 rounded-full mt-1 inline-block ${codeTypeColor(selectedTree.treeType)}`}
                  >
                    {selectedTree.treeType}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => setShowEditModal(true)}
                    className="p-2 hover:bg-gray-800 rounded-lg transition-colors"
                    title="Edit tree"
                  >
                    <Edit3 className="w-4 h-4 text-gray-400 hover:text-amber-400" />
                  </button>
                  <button
                    onClick={() => handleDelete(selectedTree.id)}
                    disabled={deleting}
                    className="p-2 hover:bg-gray-800 rounded-lg transition-colors"
                    title="Delete tree"
                  >
                    <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                  </button>
                </div>
              </div>

              {/* Procedure & Escalation */}
                {/* Procedure */}
                {selectedTree.procedure && (
                    <div className="bg-blue-600/5 border border-blue-600/20 rounded-lg p-4">
                      <p className="text-xs text-blue-400 font-medium mb-2 uppercase tracking-wider">Procedure</p>
                      <p className="text-sm leading-relaxed whitespace-pre-wrap">{selectedTree.procedure}</p>
                    </div>
                  )}

                  {/* Escalation Path */}
                  <div>
                    <div className="flex items-center justify-between mb-3">
                      <p className="text-sm text-gray-500">Escalation Path</p>
                      <button
                        onClick={() => setShowAddNodeModal(true)}
                        className="flex items-center gap-1 px-3 py-1.5 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs transition-colors"
                      >
                        <Plus className="w-3 h-3" />
                        Add Node
                      </button>
                    </div>

                    {selectedTree.nodes.length === 0 ? (
                      <div className="flex items-center gap-2 text-sm text-gray-500 py-4">
                        <AlertTriangle className="w-4 h-4" />
                        <p>No nodes configured. Add a node to start building the escalation path.</p>
                      </div>
                    ) : (
                      <div className="relative">
                        {selectedTree.nodes
                          .sort((a, b) => a.order - b.order)
                          .map((node, i, arr) => (
                            <div key={node.id} className="flex items-start gap-4 pb-6 relative group">
                              {i < arr.length - 1 && (
                                <div className="absolute left-[11px] top-6 bottom-0 w-0.5 bg-gray-700" />
                              )}
                              <div className="flex-shrink-0 w-6 h-6 rounded-full border-2 border-amber-600 flex items-center justify-center text-xs font-medium text-amber-500 bg-gray-900 z-10">
                                {node.order}
                              </div>
                              <div className="flex-1 bg-gray-800/50 rounded-lg p-3">
                                <div className="flex items-start justify-between">
                                  <div>
                                    <p className="text-sm font-medium">
                                      {node.employee
                                        ? `${node.employee.firstName} ${node.employee.lastName}`
                                        : node.roleName || 'Unassigned'}
                                    </p>
                                    <p className="text-xs text-gray-500 mt-0.5">
                                      {node.employee?.title || node.employee?.department?.name || ''}
                                    </p>
                                    {node.timeoutSeconds > 0 && (
                                      <p className="text-xs text-gray-600 mt-1">
                                        Escalates after {node.timeoutSeconds}s
                                      </p>
                                    )}
                                  </div>
                                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                    <button
                                      onClick={() => handleMoveNode(node.id, 'up')}
                                      disabled={i === 0}
                                      className="p-1 hover:bg-gray-700 rounded disabled:opacity-30"
                                      title="Move up"
                                    >
                                      <ArrowUp className="w-3.5 h-3.5 text-gray-400" />
                                    </button>
                                    <button
                                      onClick={() => handleMoveNode(node.id, 'down')}
                                      disabled={i === arr.length - 1}
                                      className="p-1 hover:bg-gray-700 rounded disabled:opacity-30"
                                      title="Move down"
                                    >
                                      <ArrowDown className="w-3.5 h-3.5 text-gray-400" />
                                    </button>
                                    <button
                                      onClick={() => handleRemoveNode(node.id)}
                                      className="p-1 hover:bg-gray-700 rounded"
                                      title="Remove node"
                                    >
                                      <X className="w-3.5 h-3.5 text-red-400" />
                                    </button>
                                  </div>
                                </div>
                              </div>
                            </div>
                          ))}
                      </div>
                    )}
                  </div>

                  {selectedTree.fallbackProcedure && (
                    <div className="bg-gray-800/50 rounded-lg p-4">
                      <p className="text-xs text-gray-500 mb-1">Fallback Procedure</p>
                      <p className="text-sm">{selectedTree.fallbackProcedure}</p>
                    </div>
                  )}
            </div>
          ) : (
            <div className="flex flex-col items-center justify-center py-12 text-gray-500">
              <PhoneCall className="w-12 h-12 mb-4 text-gray-700" />
              <p className="text-sm">Select an emergency code</p>
              <p className="text-xs mt-1">to view its procedure and escalation path</p>
            </div>
          )}
        </div>
      </div>

      {/* Create Tree Modal */}
      {showCreateModal && (
        <TreeFormModal
          title="New Phone Tree"
          onSave={handleCreate}
          onClose={() => setShowCreateModal(false)}
        />
      )}

      {/* Edit Tree Modal */}
      {showEditModal && selectedTree && (
        <TreeFormModal
          title="Edit Phone Tree"
          initial={selectedTree}
          onSave={handleUpdate}
          onClose={() => setShowEditModal(false)}
        />
      )}

      {/* Add Node Modal */}
      {showAddNodeModal && selectedTree && (
        <AddNodeModal
          nextOrder={(selectedTree.nodes.length || 0) + 1}
          onSave={handleAddNode}
          onClose={() => setShowAddNodeModal(false)}
        />
      )}

    </div>
  )
}

function TreeFormModal({
  title,
  initial,
  onSave,
  onClose,
}: {
  title: string
  initial?: PhoneTree
  onSave: (data: Partial<PhoneTree>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState(initial?.name || '')
  const [treeType, setTreeType] = useState(initial?.treeType || 'code-blue')
  const [procedure, setProcedure] = useState(initial?.procedure || '')
  const [fallback, setFallback] = useState(initial?.fallbackProcedure || '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Name is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        name: name.trim(),
        treeType: treeType as PhoneTree['treeType'],
        procedure: procedure.trim() || undefined,
        fallbackProcedure: fallback.trim() || undefined,
      })
    } catch { setError('Failed to save.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4 max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{title}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Code Name</label>
            <input type="text" required value={name} onChange={(e) => setName(e.target.value)}
              placeholder="e.g., Code Blue — Cardiac Arrest"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Code Type</label>
            <select value={treeType} onChange={(e) => setTreeType(e.target.value as typeof treeType)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
              <optgroup label="Hospital Emergency Codes">
                <option value="code-blue">Code Blue — Cardiac Arrest</option>
                <option value="code-red">Code Red — Fire</option>
                <option value="code-green">Code Green — Evacuation</option>
                <option value="code-silver">Code Silver — Active Threat</option>
                <option value="code-grey">Code Grey — Severe Weather</option>
                <option value="code-pink">Code Pink — Infant Abduction</option>
              </optgroup>
              <optgroup label="General">
                <option value="emergency">Emergency</option>
                <option value="department">Department</option>
                <option value="oncall">On-Call</option>
                <option value="admin">Admin</option>
              </optgroup>
            </select>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Procedure (what to do for this code)</label>
            <textarea value={procedure} onChange={(e) => setProcedure(e.target.value)} rows={4}
              placeholder="Describe the response protocol, who to notify, and what actions to take..."
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Fallback Procedure (optional)</label>
            <textarea value={fallback} onChange={(e) => setFallback(e.target.value)} rows={2}
              placeholder="What to do if no one responds to the escalation"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none" />
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose} className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : 'Save'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

function AddNodeModal({
  nextOrder,
  onSave,
  onClose,
}: {
  nextOrder: number
  onSave: (data: Partial<PhoneTreeNode>) => Promise<void>
  onClose: () => void
}) {
  const [employeeId, setEmployeeId] = useState('')
  const [roleName, setRoleName] = useState('')
  const [timeoutSeconds, setTimeoutSeconds] = useState(30)
  const [condition, setCondition] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!employeeId && !roleName) { setError('Employee or role name is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        order: nextOrder,
        employeeId: employeeId || undefined,
        roleName: roleName || undefined,
        timeoutSeconds,
        condition: condition || undefined,
      })
    } catch { setError('Failed to save node.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-md mx-4 max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Add Escalation Node</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Employee ID (or leave blank for role-based)</label>
            <input type="text" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}
              placeholder="Enter employee email or ID"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Role Name (if no specific employee)</label>
            <input type="text" value={roleName} onChange={(e) => setRoleName(e.target.value)}
              placeholder="e.g., Department Head, Attending"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Timeout (seconds)</label>
              <input type="number" value={timeoutSeconds} onChange={(e) => setTimeoutSeconds(Number(e.target.value))}
                min={0} max={300}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Order</label>
              <p className="text-sm text-gray-400 pt-2">{nextOrder}</p>
            </div>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Condition (optional)</label>
            <input type="text" value={condition} onChange={(e) => setCondition(e.target.value)}
              placeholder="e.g., After hours, Weekends"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose} className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Plus className="w-4 h-4" />}
              {saving ? 'Adding...' : 'Add Node'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

// ─── EVENT LOG SECTION ────────────────────────────────────────────────────

