import { Outlet } from 'react-router-dom';
import { useAuth } from '../auth/KeycloakProvider';
import { LayoutDashboard, LogOut, Shield, Activity } from 'lucide-react';

export default function DashboardLayout() {
  const { parsedToken, logout } = useAuth();
  
  // Extract tenant ID. In Keycloak, custom claims are usually mapped. 
  // Based on the export, test users have `tenant_id` attribute.
  const rawTenant = (parsedToken as any)?.tenant_id;
  const tenantId = Array.isArray(rawTenant) ? rawTenant[0] : rawTenant || 'Unknown Tenant';
  const email = parsedToken?.email || 'user@example.com';

  return (
    <div className="flex h-screen overflow-hidden bg-zinc-950 text-zinc-100 font-sans antialiased">
      {/* Sidebar */}
      <aside className="w-64 flex flex-col border-r border-zinc-800/60 bg-zinc-950/50 backdrop-blur-xl">
        <div className="flex h-16 items-center gap-3 px-6 border-b border-zinc-800/60">
          <Shield className="h-6 w-6 text-emerald-500" />
          <span className="font-semibold tracking-tight">Privacy Analytics</span>
        </div>

        <div className="px-4 py-6 flex-1 flex flex-col gap-2">
          <div className="mb-4">
            <p className="text-xs font-semibold text-zinc-500 uppercase tracking-wider mb-2 px-2">Tenant Context</p>
            <div className="px-3 py-2 bg-emerald-500/10 border border-emerald-500/20 rounded-lg">
              <p className="text-xs text-emerald-400 truncate" title={String(tenantId)}>{tenantId}</p>
            </div>
          </div>

          <nav className="space-y-1">
            <a href="#" className="flex items-center gap-3 px-3 py-2 text-sm font-medium text-emerald-400 bg-emerald-500/10 rounded-md">
              <LayoutDashboard className="h-4 w-4" />
              Dashboard
            </a>
            <a href="#" className="flex items-center gap-3 px-3 py-2 text-sm font-medium text-zinc-400 hover:text-zinc-100 hover:bg-zinc-800/50 rounded-md transition-colors">
              <Activity className="h-4 w-4" />
              Real-time Traffic
            </a>
          </nav>
        </div>

        <div className="p-4 border-t border-zinc-800/60">
          <div className="flex items-center gap-3 px-2 mb-4">
            <div className="h-8 w-8 rounded-full bg-zinc-800 flex items-center justify-center text-sm font-medium text-zinc-300">
              {email.charAt(0).toUpperCase()}
            </div>
            <div className="flex flex-col flex-1 min-w-0">
              <span className="text-sm font-medium truncate">{email}</span>
            </div>
          </div>
          <button 
            onClick={() => logout()}
            className="flex w-full items-center justify-center gap-2 px-3 py-2 text-sm font-medium text-zinc-400 hover:text-zinc-100 hover:bg-zinc-800/50 rounded-md transition-colors"
          >
            <LogOut className="h-4 w-4" />
            Sign out
          </button>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 overflow-auto relative">
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_at_top,_var(--tw-gradient-stops))] from-zinc-900 via-zinc-950 to-zinc-950 -z-10" />
        <div className="h-full">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
