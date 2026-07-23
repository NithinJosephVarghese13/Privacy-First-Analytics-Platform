import { AreaChart, XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip, ResponsiveContainer, Area } from 'recharts';
import { Users, UserCheck, Loader2 } from 'lucide-react';
import { useState, useEffect } from 'react';
import { useAuth } from '../../auth/KeycloakProvider';
import { AskAiPanel } from '../../components/AskAi/AskAiPanel';

interface VisitorsData {
  exactTier2Uniques: number;
  estimatedTier1Uniques: number;
}

interface PageviewData {
  date: string;
  pageviews: number;
}

export default function DashboardPage() {
  const { token } = useAuth();
  
  const [visitors, setVisitors] = useState<VisitorsData>({ exactTier2Uniques: 0, estimatedTier1Uniques: 0 });
  const [pageviews, setPageviews] = useState<any[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    if (!token) return;

    let active = true;

    async function fetchData() {
      try {
        const headers = { Authorization: `Bearer ${token}` };
        
        const [visitorsRes, pageviewsRes] = await Promise.all([
          fetch('http://localhost:5115/api/v1/analytics/visitors', { headers }),
          fetch('http://localhost:5115/api/v1/analytics/pageviews', { headers })
        ]);

        if (visitorsRes.ok && pageviewsRes.ok && active) {
          const vData = await visitorsRes.json();
          const pData = await pageviewsRes.json();

          // Format dates for display
          const formattedPageviews = pData.map((p: PageviewData) => ({
            name: new Date(p.date).toLocaleDateString('en-US', { weekday: 'short' }),
            pageviews: p.pageviews
          }));

          setVisitors(vData);
          setPageviews(formattedPageviews.length > 0 ? formattedPageviews : []);
        }
      } catch (error) {
        console.error('Failed to fetch analytics data', error);
      } finally {
        if (active) setIsLoading(false);
      }
    }

    fetchData();

    return () => { active = false; };
  }, [token]);

  return (
    <div className="p-8 max-w-[1600px] mx-auto flex gap-8 h-[calc(100vh-2rem)]">
      {/* Left Column: Metrics and Charts */}
      <div className="flex-1 flex flex-col gap-6 min-w-0">
        <header className="mb-2 flex justify-between items-end">
          <div>
            <h1 className="text-3xl font-bold tracking-tight text-white">Overview</h1>
            <p className="text-zinc-400 mt-1">Real-time privacy-first web traffic analysis.</p>
          </div>
          {isLoading && <Loader2 className="w-5 h-5 text-zinc-500 animate-spin" />}
        </header>

        {/* Metric Cards */}
        <div className="grid grid-cols-2 gap-4">
          <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm relative overflow-hidden group">
            <div className="absolute top-0 right-0 p-4 opacity-10 group-hover:opacity-20 transition-opacity">
              <UserCheck className="w-16 h-16 text-emerald-500" />
            </div>
            <div className="flex items-center justify-between mb-2">
              <div className="flex items-center gap-2 text-emerald-400">
                <UserCheck className="w-4 h-4" />
                <span className="text-sm font-medium">Known Unique Visitors</span>
              </div>
              <span className="text-[10px] bg-emerald-500/10 text-emerald-400 border border-emerald-500/20 px-2 py-0.5 rounded font-mono uppercase tracking-wider">
                Exact (Tier 2)
              </span>
            </div>
            <div className="flex items-baseline gap-2 mt-4">
              <span className="text-4xl font-bold tracking-tighter text-white">
                {visitors.exactTier2Uniques.toLocaleString()}
              </span>
            </div>
            <p className="text-xs text-zinc-500 mt-2">Durable HMAC match (Opted-in / Authenticated)</p>
          </div>

          <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm relative overflow-hidden group">
            <div className="absolute top-0 right-0 p-4 opacity-10 group-hover:opacity-20 transition-opacity">
              <Users className="w-16 h-16 text-blue-500" />
            </div>
            <div className="flex items-center justify-between mb-2">
              <div className="flex items-center gap-2 text-blue-400">
                <Users className="w-4 h-4" />
                <span className="text-sm font-medium">Estimated Anonymous Visitors</span>
              </div>
              <span className="text-[10px] bg-amber-500/20 text-amber-300 border border-amber-500/40 px-2 py-0.5 rounded font-semibold uppercase tracking-wider shadow-sm">
                Estimated (Tier 1)
              </span>
            </div>
            <div className="flex items-baseline gap-2 mt-4">
              <span className="text-4xl font-bold tracking-tighter text-white">
                {visitors.estimatedTier1Uniques.toLocaleString()}
              </span>
            </div>
            <p className="text-xs text-zinc-400 mt-2 flex items-center gap-1">
              <span>Tier 1 HyperLogLog upper-bound estimate</span>
            </p>
          </div>
        </div>

        {/* Chart */}
        <div className="bg-zinc-900/50 border border-zinc-800/60 rounded-xl p-5 backdrop-blur-sm flex-1 min-h-[300px]">
          <h3 className="text-sm font-medium text-zinc-400 mb-6">Pageviews Over Time</h3>
          <ResponsiveContainer width="100%" height="90%">
            <AreaChart data={pageviews} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
              <defs>
                <linearGradient id="colorPageviews" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="#8b5cf6" stopOpacity={0.3}/>
                  <stop offset="95%" stopColor="#8b5cf6" stopOpacity={0}/>
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" stroke="#27272a" vertical={false} />
              <XAxis dataKey="name" stroke="#52525b" fontSize={12} tickLine={false} axisLine={false} />
              <YAxis stroke="#52525b" fontSize={12} tickLine={false} axisLine={false} tickFormatter={(value) => value >= 1000 ? `${value / 1000}k` : value} />
              <RechartsTooltip 
                contentStyle={{ backgroundColor: '#18181b', borderColor: '#27272a', borderRadius: '8px', color: '#e4e4e7' }}
                itemStyle={{ color: '#e4e4e7' }}
              />
              <Area type="monotone" dataKey="pageviews" name="Pageviews" stroke="#8b5cf6" fillOpacity={1} fill="url(#colorPageviews)" strokeWidth={2} />
            </AreaChart>
          </ResponsiveContainer>
        </div>
      </div>

      {/* Right Column: Ask AI Chat Panel */}
      <AskAiPanel />
    </div>
  );
}

