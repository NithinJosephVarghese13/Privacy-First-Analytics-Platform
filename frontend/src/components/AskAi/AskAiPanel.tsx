import { useState, useRef, useEffect } from 'react';
import { Bot, Sparkles, Send, Loader2, Shield, Layers, Zap, AlertCircle, RefreshCw } from 'lucide-react';
import { useAuth } from '../../auth/KeycloakProvider';
import { AskAiDataTable } from './AskAiDataTable';

interface AskAiResponse {
  prompt: string;
  generatedSql: string;
  isCachedResponse: boolean;
  data: Array<Record<string, any>>;
}

interface ChatMessage {
  id: string;
  sender: 'user' | 'ai';
  prompt?: string;
  response?: AskAiResponse;
  error?: string;
  timestamp: Date;
}

const SUGGESTED_PROMPTS = [
  'Top 5 pages by unique visitors this week',
  'Daily pageviews for the past month',
  'Known vs estimated unique visitors count',
  'Top traffic sources by domain'
];

export function AskAiPanel() {
  const { token } = useAuth();
  const [query, setQuery] = useState('');
  const [useCache, setUseCache] = useState(true);
  const [isLoading, setIsLoading] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, isLoading]);

  const handleSubmit = async (promptText: string) => {
    const trimmedPrompt = promptText.trim();
    if (!trimmedPrompt || isLoading) return;

    const userMessage: ChatMessage = {
      id: Guid(),
      sender: 'user',
      prompt: trimmedPrompt,
      timestamp: new Date()
    };

    setMessages((prev) => [...prev, userMessage]);
    setQuery('');
    setIsLoading(true);

    try {
      const response = await fetch('http://localhost:5115/api/v1/analytics/ask-ai', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${token}`
        },
        body: JSON.stringify({
          prompt: trimmedPrompt,
          useCache
        })
      });

      if (!response.ok) {
        const errJson = await response.json().catch(() => ({}));
        throw new Error(errJson.error || `Server error: ${response.status}`);
      }

      const aiData: AskAiResponse = await response.json();

      const aiMessage: ChatMessage = {
        id: Guid(),
        sender: 'ai',
        response: aiData,
        timestamp: new Date()
      };

      setMessages((prev) => [...prev, aiMessage]);
    } catch (err: any) {
      const errorMessage: ChatMessage = {
        id: Guid(),
        sender: 'ai',
        error: err.message || 'Failed to process AI query.',
        timestamp: new Date()
      };
      setMessages((prev) => [...prev, errorMessage]);
    } finally {
      setIsLoading(false);
    }
  };

  function Guid() {
    return Math.random().toString(36).substring(2, 9);
  }

  return (
    <div className="w-[420px] bg-zinc-900/40 border border-zinc-800/60 rounded-xl flex flex-col h-full overflow-hidden backdrop-blur-md shadow-2xl">
      {/* Panel Header */}
      <div className="p-4 border-b border-zinc-800/60 bg-zinc-900/80 flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="p-2 bg-indigo-500/10 text-indigo-400 rounded-lg border border-indigo-500/20">
            <Bot className="w-5 h-5" />
          </div>
          <div>
            <h3 className="font-semibold text-white text-sm tracking-tight flex items-center gap-2">
              Ask AI
              <span className="text-[10px] bg-indigo-500/20 text-indigo-300 border border-indigo-500/30 px-1.5 py-0.2 rounded font-normal">
                RLS Scoped
              </span>
            </h3>
            <p className="text-xs text-zinc-400">Natural language SQL insights</p>
          </div>
        </div>

        {/* Live / Cached Demo Toggle */}
        <button
          type="button"
          onClick={() => setUseCache(!useCache)}
          title={useCache ? "Using Demo Cache (zero latency, high reliability)" : "Using Live DeepSeek AI"}
          className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium border transition-all cursor-pointer ${
            useCache
              ? 'bg-amber-500/10 text-amber-300 border-amber-500/30 hover:bg-amber-500/20'
              : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/30 hover:bg-emerald-500/20'
          }`}
        >
          {useCache ? <Layers className="w-3.5 h-3.5" /> : <Zap className="w-3.5 h-3.5" />}
          <span>{useCache ? 'Demo Cache' : 'Live AI'}</span>
        </button>
      </div>

      {/* Chat Messages Feed */}
      <div className="flex-1 p-4 overflow-y-auto space-y-4">
        {/* Default Welcome / Instructions */}
        <div className="flex gap-3">
          <div className="w-8 h-8 rounded-full bg-indigo-500/20 flex items-center justify-center flex-shrink-0 border border-indigo-500/30">
            <Sparkles className="w-4 h-4 text-indigo-400" />
          </div>
          <div className="bg-zinc-800/60 p-3.5 rounded-2xl rounded-tl-none border border-zinc-700/50 text-sm text-zinc-300 shadow-sm leading-relaxed">
            <p>Hello! Ask any question about your tenant's web traffic.</p>
            <p className="text-xs text-zinc-400 mt-1">
              Queries run against restricted reporting views inside Postgres RLS isolation.
            </p>
          </div>
        </div>

        {/* Suggested Prompts (shown when history is empty or low) */}
        {messages.length === 0 && (
          <div className="pt-2 space-y-2">
            <p className="text-[11px] font-semibold text-zinc-500 uppercase tracking-wider mb-2 px-1">
              Suggested queries
            </p>
            <div className="grid grid-cols-1 gap-2">
              {SUGGESTED_PROMPTS.map((promptText, i) => (
                <button
                  key={i}
                  onClick={() => handleSubmit(promptText)}
                  className="w-full text-left p-2.5 rounded-xl border border-zinc-800 bg-zinc-900/40 hover:bg-zinc-800/80 hover:border-indigo-500/40 transition-all text-xs text-zinc-300 group shadow-sm flex items-center justify-between cursor-pointer"
                >
                  <span className="truncate">{promptText}</span>
                  <span className="text-indigo-400 group-hover:translate-x-0.5 transition-transform">→</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {/* Message Items */}
        {messages.map((msg) => (
          <div key={msg.id} className="space-y-3">
            {msg.sender === 'user' ? (
              <div className="flex justify-end">
                <div className="bg-indigo-600/30 border border-indigo-500/40 text-white px-4 py-2.5 rounded-2xl rounded-tr-none text-sm max-w-[85%] shadow-sm">
                  {msg.prompt}
                </div>
              </div>
            ) : msg.error ? (
              <div className="flex gap-3">
                <div className="w-8 h-8 rounded-full bg-rose-500/20 flex items-center justify-center flex-shrink-0 border border-rose-500/30">
                  <AlertCircle className="w-4 h-4 text-rose-400" />
                </div>
                <div className="bg-rose-500/10 border border-rose-500/20 p-3.5 rounded-2xl rounded-tl-none text-xs text-rose-300 space-y-1">
                  <p className="font-semibold text-rose-400">Query Failed</p>
                  <p>{msg.error}</p>
                </div>
              </div>
            ) : (
              <div className="flex gap-3">
                <div className="w-8 h-8 rounded-full bg-indigo-500/20 flex items-center justify-center flex-shrink-0 border border-indigo-500/30">
                  <Sparkles className="w-4 h-4 text-indigo-400" />
                </div>
                <div className="bg-zinc-800/60 p-3.5 rounded-2xl rounded-tl-none border border-zinc-700/50 text-xs text-zinc-300 space-y-2 flex-1 min-w-0 shadow-sm">
                  <p className="text-zinc-200 text-sm font-medium">
                    "{msg.response?.prompt}"
                  </p>
                  {msg.response && (
                    <AskAiDataTable
                      data={msg.response.data}
                      generatedSql={msg.response.generatedSql}
                      isCachedResponse={msg.response.isCachedResponse}
                    />
                  )}
                </div>
              </div>
            )}
          </div>
        ))}

        {/* Loading Indicator */}
        {isLoading && (
          <div className="flex gap-3 items-center">
            <div className="w-8 h-8 rounded-full bg-indigo-500/20 flex items-center justify-center flex-shrink-0 border border-indigo-500/30">
              <Loader2 className="w-4 h-4 text-indigo-400 animate-spin" />
            </div>
            <div className="bg-zinc-800/40 px-4 py-2.5 rounded-2xl rounded-tl-none border border-zinc-700/40 text-xs text-zinc-400 flex items-center gap-2">
              <span>Generating SQL & querying Postgres...</span>
            </div>
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      {/* Footer / Input Area */}
      <div className="p-3 bg-zinc-900/90 border-t border-zinc-800/60">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            handleSubmit(query);
          }}
          className="relative flex items-center"
        >
          <input
            type="text"
            placeholder="Ask a question about traffic, pages, or visitors..."
            className="w-full bg-zinc-950 border border-zinc-800 rounded-lg pl-3.5 pr-10 py-2.5 text-xs text-white placeholder-zinc-500 focus:outline-none focus:ring-1 focus:ring-indigo-500/50 focus:border-indigo-500 transition-all shadow-inner"
            value={query}
            disabled={isLoading}
            onChange={(e) => setQuery(e.target.value)}
          />
          <button
            type="submit"
            disabled={!query.trim() || isLoading}
            className="absolute right-1.5 p-1.5 text-zinc-400 hover:text-indigo-300 disabled:opacity-40 disabled:hover:text-zinc-400 transition-colors bg-indigo-600/20 hover:bg-indigo-600/40 rounded-md cursor-pointer"
          >
            {isLoading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Send className="w-3.5 h-3.5" />}
          </button>
        </form>

        <div className="flex items-center justify-between text-[10px] text-zinc-500 mt-2 px-1">
          <span className="flex items-center gap-1">
            <Shield className="w-3 h-3 text-emerald-500" />
            PostgreSQL Row-Level Security Enforced
          </span>

          {messages.length > 0 && (
            <button
              onClick={() => setMessages([])}
              className="flex items-center gap-1 hover:text-zinc-300 transition-colors cursor-pointer"
            >
              <RefreshCw className="w-2.5 h-2.5" />
              Clear history
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
