import Link from "next/link";

const capabilities = [
  ["Knowledge", "Ground answers in workspace files and company context."],
  ["Actions", "Connect tools and complete work with human approval."],
  ["Traceability", "See what the AI used, called, changed and produced."],
];

export default function Home() {
  return (
    <main className="min-h-screen overflow-hidden bg-[#08090b] text-white">
      <div className="mx-auto max-w-7xl px-6">
        <header className="flex h-20 items-center justify-between border-b border-white/10">
          <div className="text-sm font-semibold tracking-[0.28em]">ICEHOTT</div>
          <nav className="flex items-center gap-3">
            <Link href="/login" className="rounded-full px-4 py-2 text-sm text-white/65 hover:text-white">Sign in</Link>
            <Link href="/register" className="rounded-full bg-white px-5 py-2.5 text-sm font-medium text-black hover:bg-amber-100">Start workspace</Link>
          </nav>
        </header>

        <section className="grid min-h-[680px] items-center gap-16 py-20 lg:grid-cols-[1.05fr_0.95fr]">
          <div>
            <p className="mb-5 text-xs font-medium uppercase tracking-[0.34em] text-amber-200/70">AI workspace · built for execution</p>
            <h1 className="max-w-4xl text-5xl font-medium leading-[1.05] tracking-[-0.04em] sm:text-6xl lg:text-7xl">One AI layer for the work that actually matters.</h1>
            <p className="mt-7 max-w-2xl text-lg leading-8 text-white/52">ICEHOTT brings knowledge, tools, approvals and AI workflows into one secure workspace. Answers are only the beginning.</p>
            <div className="mt-9 flex flex-wrap gap-3">
              <Link href="/register" className="rounded-full bg-white px-6 py-3 text-sm font-semibold text-black hover:bg-amber-100">Create your workspace</Link>
              <a href="#architecture" className="rounded-full border border-white/12 px-6 py-3 text-sm text-white/70 hover:bg-white/5 hover:text-white">See the foundation</a>
            </div>
          </div>

          <div className="relative rounded-[28px] border border-white/10 bg-white/[0.035] p-4 shadow-2xl shadow-black/40">
            <div className="grid min-h-[480px] grid-cols-[150px_1fr] overflow-hidden rounded-2xl border border-white/8 bg-[#0d0f12]">
              <aside className="border-r border-white/8 p-4">
                <div className="mb-8 text-xs font-semibold tracking-[0.2em]">ICEHOTT</div>
                {["Home", "Agents", "Tasks", "Knowledge", "Files"].map((item, index) => (
                  <div key={item} className={`mb-1 rounded-lg px-3 py-2 text-xs ${index === 0 ? "bg-white/8 text-white" : "text-white/35"}`}>{item}</div>
                ))}
              </aside>
              <div className="flex flex-col p-6">
                <div className="mb-auto">
                  <span className="rounded-full border border-emerald-300/15 bg-emerald-300/5 px-3 py-1 text-[10px] text-emerald-200/70">Secure workspace ready</span>
                  <h2 className="mt-8 max-w-sm text-3xl font-medium leading-tight">What should ICEHOTT help your team accomplish?</h2>
                </div>
                <div className="rounded-2xl border border-white/10 bg-black/30 p-4 text-sm text-white/30">Ask ICEHOTT… <span className="float-right text-white/20">↵</span></div>
              </div>
            </div>
          </div>
        </section>

        <section id="architecture" className="border-t border-white/10 py-20">
          <div className="mb-10 max-w-2xl">
            <p className="text-xs uppercase tracking-[0.3em] text-white/35">Foundation</p>
            <h2 className="mt-4 text-3xl font-medium">Built like a system, not a demo.</h2>
          </div>
          <div className="grid gap-4 md:grid-cols-3">
            {capabilities.map(([title, description]) => (
              <article key={title} className="rounded-2xl border border-white/10 bg-white/[0.025] p-6">
                <h3 className="text-base font-medium">{title}</h3>
                <p className="mt-3 text-sm leading-6 text-white/45">{description}</p>
              </article>
            ))}
          </div>
        </section>
      </div>
    </main>
  );
}
