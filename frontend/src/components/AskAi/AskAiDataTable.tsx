import { useState } from 'react';
import { Database, Code2, Check, Copy, Sparkles, Layers } from 'lucide-react';

interface AskAiDataTableProps {
  data: Array<Record<string, any>>;
  generatedSql: string;
  isCachedResponse: boolean;
}

export function AskAiDataTable({ data, generatedSql, isCachedResponse }: AskAiDataTableProps) {
  const [showSql, setShowSql] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleCopySql = () => {
    navigator.clipboard.writeText(generatedSql);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  // Dynamically extract unique keys across all row objects
  const columns = data.length > 0 
    ? Array.from(new Set(data.flatMap((row) => Object.keys(row))))
    : [];

  const formatHeader = (key: string) => {
    return key
      .replace(/_/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/\b\w/g, (char) => char.toUpperCase());
  };

  const formatCellValue = (value: any) => {
    if (value === null || value === undefined) {
      return <span className="text-zinc-600 italic">null</span>;
    }
    if (typeof value === 'boolean') {
      return (
        <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium ${value ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/20' : 'bg-zinc-800 text-zinc-400'}`}>
          {value ? 'true' : 'false'}
        </span>
      );
    }
    if (typeof value === 'number') {
      return <span className="font-mono text-emerald-400">{value.toLocaleString()}</span>;
    }
    if (typeof value === 'string') {
      // Check if ISO date string
      if (/^\d{4}-\d{2}-\d{2}/.test(value)) {
        return new Date(value).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
      }
      return value;
    }
    return JSON.stringify(value);
  };

  return (
    <div className="space-y-3 w-full my-2">
      {/* Action / Badges Header */}
      <div className="flex items-center justify-between gap-2 text-xs flex-wrap">
        <div className="flex items-center gap-2">
          <span className="flex items-center gap-1 font-mono text-[11px] text-zinc-400 bg-zinc-900 border border-zinc-800 px-2 py-0.5 rounded-md">
            <Database className="w-3 h-3 text-indigo-400" />
            {data.length} {data.length === 1 ? 'row' : 'rows'}
          </span>

          <span className={`flex items-center gap-1 text-[11px] px-2 py-0.5 rounded-md border font-medium ${
            isCachedResponse 
              ? 'bg-amber-500/10 text-amber-400 border-amber-500/20'
              : 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20'
          }`}>
            {isCachedResponse ? <Layers className="w-3 h-3" /> : <Sparkles className="w-3 h-3" />}
            {isCachedResponse ? 'Cached' : 'Live DeepSeek'}
          </span>
        </div>

        <button
          onClick={() => setShowSql(!showSql)}
          className="flex items-center gap-1.5 text-[11px] text-zinc-400 hover:text-white bg-zinc-900 hover:bg-zinc-800 border border-zinc-800 px-2.5 py-1 rounded-md transition-colors cursor-pointer"
        >
          <Code2 className="w-3 h-3 text-indigo-400" />
          {showSql ? 'Hide SQL' : 'View SQL'}
        </button>
      </div>

      {/* Generated SQL Code Block */}
      {showSql && (
        <div className="relative rounded-lg border border-zinc-800 bg-zinc-950/90 p-3 font-mono text-xs text-emerald-400 shadow-inner group">
          <div className="flex items-center justify-between text-[10px] text-zinc-500 mb-1 border-b border-zinc-800/80 pb-1">
            <span>EXECUTED TENANT-SCOPED SQL</span>
            <button
              onClick={handleCopySql}
              className="flex items-center gap-1 text-zinc-400 hover:text-zinc-200 transition-colors cursor-pointer"
              title="Copy SQL"
            >
              {copied ? <Check className="w-3 h-3 text-emerald-400" /> : <Copy className="w-3 h-3" />}
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
          <pre className="overflow-x-auto whitespace-pre-wrap break-all py-1 font-mono text-[11px] leading-relaxed text-zinc-200">
            {generatedSql}
          </pre>
        </div>
      )}

      {/* Data Table */}
      {data.length === 0 ? (
        <div className="p-4 text-center text-xs text-zinc-500 border border-dashed border-zinc-800 rounded-lg bg-zinc-900/20">
          No records returned for this query.
        </div>
      ) : (
        <div className="max-h-64 overflow-auto rounded-lg border border-zinc-800/80 bg-zinc-950/60 shadow-sm">
          <table className="w-full text-left border-collapse text-xs">
            <thead className="sticky top-0 bg-zinc-900/90 backdrop-blur-md border-b border-zinc-800/80 text-zinc-400 font-medium">
              <tr>
                {columns.map((col) => (
                  <th key={col} className="px-3 py-2 text-[11px] uppercase tracking-wider font-semibold whitespace-nowrap">
                    {formatHeader(col)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-zinc-800/40 text-zinc-300">
              {data.map((row, idx) => (
                <tr key={idx} className="hover:bg-zinc-800/30 transition-colors even:bg-zinc-900/20">
                  {columns.map((col) => (
                    <td key={col} className="px-3 py-2 whitespace-nowrap">
                      {formatCellValue(row[col])}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
