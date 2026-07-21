import { AreaChart, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, ResponsiveContainer, Area } from 'recharts';
import { Bot, Sparkles, Users, UserCheck, ArrowUpRight, Shield } from 'lucide-react';
import { useState } from 'react';

const MOCK_DATA = [
  { name: 'Mon', known: 4000, estimated: 6400 },
  { name: 'Tue', known: 3000, estimated: 5398 },
  { name: 'Wed', known: 2000, estimated: 9800 },
  { name: 'Thu', known: 2780, estimated: 3908 },
  { name: 'Fri', known: 1890, estimated: 4800 },
  { name: 'Sat', known: 2390, estimated: 3800 },
  { name: 'Sun', known: 3490, estimated: 4300 },
];

export default function DashboardPage() {
  const [query, setQuery] = useState('');

  return (
    <div className="p-8 max-w-7xl mx-auto flex gap-8 h-full">
      {/* Left Column: Metrics and Charts */}
      <div className="flex-1 flex flex-col gap-6">
        <header className="mb-2">
          <h1 className="text-3xl font-bold tracking-tight text-white">Overview</h1>
          <p className="text-zinc-400 mt-1">Real-time privacy-first web traffic analysis.</p>
        </header>

        {/* Metric Cards */}
        <div className="grid grid-cols-2 gap-4">
          <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm relative overflow-hidden group">
            <div className="absolute top-0 right-0 p-4 opacity-10 group-hover:opacity-20 transition-opacity">
              <UserCheck className="w-16 h-16 text-emerald-500" />
            </div>
            <div className="flex items-center gap-2 text-emerald-400 mb-2">
              <UserCheck className="w-4 h-4" />
              <span className="text-sm font-medium">Known Unique Visitors</span>
            </div>
            <div className="flex items-baseline gap-2 mt-4">
              <span className="text-4xl font-bold tracking-tighter text-white">12,405</span>
              <span className="text-sm font-medium text-emerald-500 flex items-center">
                <ArrowUpRight className="w-3 h-3 mr-1" />
                12%
              </span>
            </div>
            <p className="text-xs text-zinc-500 mt-2">Tier 2 exactly matched</p>
          </div>

          <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm relative overflow-hidden group">
            <div className="absolute top-0 right-0 p-4 opacity-10 group-hover:opacity-20 transition-opacity">
              <Users className="w-16 h-16 text-blue-500" />
            </div>
            <div className="flex items-center gap-2 text-blue-400 mb-2">
              <Users className="w-4 h-4" />
              <span className="text-sm font-medium">Estimated Anonymous Visitors</span>
            </div>
            <div className="flex items-baseline gap-2 mt-4">
              <span className="text-4xl font-bold tracking-tighter text-white">38,409</span>
              <span className="text-sm font-medium text-blue-400 flex items-center">
                <ArrowUpRight className="w-3 h-3 mr-1" />
                8%
              </span>
            </div>
            <p className="text-xs text-zinc-500 mt-2">Tier 1 HLL upper-bound (Approximate)</p>
          </div>
        </div>

        {/* Chart */}
        <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm flex-1 min-h-[300px]">
          <h3 className="text-sm font-medium text-zinc-400 mb-6">Traffic Over Time</h3>
          <ResponsiveContainer width="100%" height="90%">
            <AreaChart data={MOCK_DATA} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
              <defs>
                <linearGradient id="colorKnown" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="#10b981" stopOpacity={0.3}/>
                  <stop offset="95%" stopColor="#10b981" stopOpacity={0}/>
                </linearGradient>
                <linearGradient id="colorEst" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="#3b82f6" stopOpacity={0.3}/>
                  <stop offset="95%" stopColor="#3b82f6" stopOpacity={0}/>
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" vertical={false} />
              <XAxis dataKey="name" stroke="#52525b" fontSize={12} tickLine={false} axisLine={false} />
              <YAxis stroke="#52525b" fontSize={12} tickLine={false} axisLine={false} tickFormatter={(value) => `${value / 1000}k`} />
              <RechartsTooltip 
                contentStyle={{ backgroundColor: '#18181b', borderColor: '#27272a', borderRadius: '8px', color: '#e4e4e7' }}
                itemStyle={{ color: '#e4e4e7' }}
              />
              <Area type="monotone" dataKey="estimated" name="Estimated Anonymous" stroke="#3b82f6" fillOpacity={1} fill="url(#colorEst)" strokeWidth={2} />
              <Area type="monotone" dataKey="known" name="Known Unique" stroke="#10b981" fillOpacity={1} fill="url(#colorKnown)" strokeWidth={2} />
            </AreaChart>
          </ResponsiveContainer>
        </div>
      </div>

      {/* Right Column: AI Chat Panel */}
      <div className="w-[380px] bg-zinc-900/40 border border-zinc-800/60 rounded-xl flex flex-col overflow-hidden backdrop-blur-md shadow-2xl">
        <div className="p-4 border-b border-zinc-800/60 bg-zinc-900/80 flex items-center gap-3">
          <div className="p-2 bg-indigo-500/10 text-indigo-400 rounded-lg">
            <Bot className="w-5 h-5" />
          </div>
          <div>
            <h3 className="font-medium text-white text-sm">Ask AI</h3>
            <p className="text-xs text-zinc-400">Natural language insights</p>
          </div>
        </div>

        <div className="flex-1 p-4 overflow-y-auto space-y-4">
          <div className="flex gap-3">
            <div className="w-8 h-8 rounded-full bg-indigo-500/20 flex items-center justify-center flex-shrink-0">
              <Sparkles className="w-4 h-4 text-indigo-400" />
            </div>
            <div className="bg-zinc-800/50 p-3 rounded-2xl rounded-tl-none border border-zinc-700/50 text-sm text-zinc-300 shadow-sm">
              Hello! I can help you analyze your tenant's web traffic. Try asking me a question.
            </div>
          </div>
          
          {/* Preset Prompts */}
          <div className="pt-4 space-y-2">
            <p className="text-xs font-medium text-zinc-500 uppercase tracking-wider mb-3 px-1">Suggested queries</p>
            {['Top 5 pages by unique visitors this week', 'Show traffic peaks for last month', 'Compare Tier 1 vs Tier 2 growth'].map((text, i) => (
              <button key={i} className="w-full text-left p-3 rounded-xl border border-zinc-800 bg-zinc-900/30 hover:bg-zinc-800 hover:border-zinc-700 transition-all text-sm text-zinc-300 group shadow-sm">
                <span className="text-indigo-400 mr-2 group-hover:text-indigo-300 transition-colors">→</span>
                {text}
              </button>
            ))}
          </div>
        </div>

        <div className="p-4 bg-zinc-900/80 border-t border-zinc-800/60">
          <div className="relative">
            <input 
              type="text" 
              placeholder="Ask a question..." 
              className="w-full bg-zinc-950 border border-zinc-800 rounded-lg pl-4 pr-10 py-3 text-sm text-white placeholder-zinc-500 focus:outline-none focus:ring-1 focus:ring-indigo-500/50 focus:border-indigo-500 transition-all shadow-inner"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
            <button className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 text-zinc-400 hover:text-indigo-400 transition-colors bg-zinc-900 hover:bg-zinc-800 rounded-md">
              <Sparkles className="w-4 h-4" />
            </button>
          </div>
          <p className="text-[10px] text-zinc-500 mt-2 text-center flex items-center justify-center gap-1">
            <Shield className="w-3 h-3" />
            Queries run against isolated, tenant-scoped views.
          </p>
        </div>
      </div>
    </div>
  );
}
