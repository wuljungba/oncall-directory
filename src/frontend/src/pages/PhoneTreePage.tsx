import { useState, useEffect } from 'react'
import { PhoneCall, ChevronRight, AlertTriangle } from 'lucide-react'
import { directoryApi } from '@/services/api'
import type { PhoneTree } from '@/types'

export default function PhoneTreePage() {
  const [trees, setTrees] = useState<PhoneTree[]>([])
  const [selectedTree, setSelectedTree] = useState<PhoneTree | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    directoryApi
      .getPhoneTrees()
      .then((data) => {
        setTrees(data)
        setLoading(false)
      })
      .catch(() => setLoading(false))
  }, [])

  const treeTypeColor = (type: string) => {
    switch (type) {
      case 'emergency': return 'bg-red-600/20 text-red-500'
      case 'department': return 'bg-blue-600/20 text-blue-500'
      case 'oncall': return 'bg-amber-600/20 text-amber-500'
      case 'admin': return 'bg-purple-600/20 text-purple-500'
      default: return 'bg-gray-600/20 text-gray-500'
    }
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Phone Trees</h1>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Tree List */}
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800">
            <p className="text-sm text-gray-500">{trees.length} phone trees</p>
          </div>
          <div className="divide-y divide-gray-800">
            {loading ? (
              <div className="flex items-center justify-center py-12">
                <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" />
              </div>
            ) : trees.length === 0 ? (
              <div className="text-center py-12 text-gray-500">
                <PhoneCall className="w-8 h-8 mx-auto mb-2 text-gray-700" />
                <p className="text-sm">No phone trees configured</p>
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
                        className={`text-xs px-2 py-0.5 rounded-full mt-1 inline-block ${treeTypeColor(tree.treeType)}`}
                      >
                        {tree.treeType}
                      </span>
                    </div>
                    <ChevronRight className="w-4 h-4 text-gray-600" />
                  </div>
                </button>
              ))
            )}
          </div>
        </div>

        {/* Tree Detail */}
        <div className="lg:col-span-2 bg-gray-900 border border-gray-800 rounded-xl p-5">
          {selectedTree ? (
            <div className="space-y-5">
              <div>
                <h2 className="text-lg font-medium">{selectedTree.name}</h2>
                <span
                  className={`text-xs px-2 py-0.5 rounded-full mt-1 inline-block ${treeTypeColor(selectedTree.treeType)}`}
                >
                  {selectedTree.treeType}
                </span>
              </div>

              {/* Escalation Path */}
              <div className="space-y-0">
                <p className="text-sm text-gray-500 mb-3">Escalation Path</p>
                {selectedTree.nodes.length === 0 ? (
                  <div className="flex items-center gap-2 text-sm text-gray-500 py-4">
                    <AlertTriangle className="w-4 h-4" />
                    <p>No nodes configured in this tree</p>
                  </div>
                ) : (
                  <div className="relative">
                    {selectedTree.nodes
                      .sort((a, b) => a.order - b.order)
                      .map((node, i) => (
                        <div key={node.id} className="flex items-start gap-4 pb-6 relative">
                          {/* Connector line */}
                          {i < selectedTree.nodes.length - 1 && (
                            <div className="absolute left-[11px] top-6 bottom-0 w-0.5 bg-gray-700" />
                          )}
                          {/* Node circle */}
                          <div className="flex-shrink-0 w-6 h-6 rounded-full border-2 border-amber-600 flex items-center justify-center text-xs font-medium text-amber-500 bg-gray-900 z-10">
                            {node.order}
                          </div>
                          <div className="flex-1 bg-gray-800/50 rounded-lg p-3">
                            <p className="text-sm font-medium">
                              {node.employee
                                ? `${node.employee.firstName} ${node.employee.lastName}`
                                : node.roleName || 'Unassigned'}
                            </p>
                            <p className="text-xs text-gray-500 mt-0.5">
                              {node.employee?.title ||
                                node.employee?.department?.name ||
                                ''}
                            </p>
                            {node.timeoutSeconds > 0 && (
                              <p className="text-xs text-gray-600 mt-1">
                                Escalates after {node.timeoutSeconds}s
                              </p>
                            )}
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
              <p className="text-sm">Select a phone tree</p>
              <p className="text-xs mt-1">to view the escalation path</p>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
