---
Description: Welcome page for new and anonymous visitors
---

<style>
.mw-landing {
  --mw-primary: #4f46e5;
  --mw-primary-light: #6366f1;
  --mw-primary-dark: #3730a3;
  --mw-accent: #06b6d4;
  --mw-bg: #ffffff;
  --mw-bg-alt: #f8fafc;
  --mw-text: #1e293b;
  --mw-text-muted: #64748b;
  --mw-card-bg: #ffffff;
  --mw-card-border: #e2e8f0;
  --mw-card-shadow: 0 1px 3px rgba(0,0,0,0.1), 0 1px 2px rgba(0,0,0,0.06);
  --mw-hero-gradient: linear-gradient(135deg, #4f46e5 0%, #06b6d4 50%, #8b5cf6 100%);
  --mw-radius: 12px;
  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
  color: var(--mw-text);
  line-height: 1.6;
}

[data-theme="dark"] .mw-landing {
  --mw-primary: #818cf8;
  --mw-primary-light: #a5b4fc;
  --mw-primary-dark: #6366f1;
  --mw-accent: #22d3ee;
  --mw-bg: #0f172a;
  --mw-bg-alt: #1e293b;
  --mw-text: #f1f5f9;
  --mw-text-muted: #94a3b8;
  --mw-card-bg: #1e293b;
  --mw-card-border: #334155;
  --mw-card-shadow: 0 1px 3px rgba(0,0,0,0.3), 0 1px 2px rgba(0,0,0,0.2);
  --mw-hero-gradient: linear-gradient(135deg, #312e81 0%, #155e75 50%, #5b21b6 100%);
}

/* Hero */
.mw-hero {
  background: var(--mw-hero-gradient);
  padding: 80px 32px;
  text-align: center;
  border-radius: var(--mw-radius);
  margin-bottom: 48px;
  position: relative;
  overflow: hidden;
}
.mw-hero::before {
  content: '';
  position: absolute;
  inset: 0;
  background: radial-gradient(circle at 20% 50%, rgba(255,255,255,0.1) 0%, transparent 50%),
              radial-gradient(circle at 80% 50%, rgba(255,255,255,0.08) 0%, transparent 50%);
}
.mw-hero h1 {
  font-size: 2.75rem;
  font-weight: 800;
  color: #ffffff;
  margin: 0 0 16px;
  position: relative;
  letter-spacing: -0.02em;
}
.mw-hero .mw-subtitle {
  font-size: 1.25rem;
  color: rgba(255,255,255,0.9);
  max-width: 600px;
  margin: 0 auto 32px;
  position: relative;
}
.mw-hero-actions {
  display: flex;
  gap: 16px;
  justify-content: center;
  flex-wrap: wrap;
  position: relative;
}
.mw-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 14px 32px;
  border-radius: 8px;
  font-size: 1rem;
  font-weight: 600;
  text-decoration: none;
  transition: transform 0.15s, box-shadow 0.15s;
  cursor: pointer;
}
.mw-btn:hover {
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(0,0,0,0.2);
}
.mw-btn-primary {
  background: #ffffff;
  color: var(--mw-primary-dark);
}
.mw-btn-secondary {
  background: rgba(255,255,255,0.15);
  color: #ffffff;
  border: 1px solid rgba(255,255,255,0.3);
  backdrop-filter: blur(4px);
}

/* Features */
.mw-features {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 24px;
  margin-bottom: 48px;
}
.mw-feature-card {
  background: var(--mw-card-bg);
  border: 1px solid var(--mw-card-border);
  border-radius: var(--mw-radius);
  padding: 28px 24px;
  box-shadow: var(--mw-card-shadow);
  transition: transform 0.15s, box-shadow 0.15s;
}
.mw-feature-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(0,0,0,0.12);
}
.mw-feature-icon {
  font-size: 2rem;
  margin-bottom: 12px;
  display: block;
}
.mw-feature-card h3 {
  font-size: 1.1rem;
  font-weight: 700;
  margin: 0 0 8px;
  color: var(--mw-text);
}
.mw-feature-card p {
  font-size: 0.925rem;
  color: var(--mw-text-muted);
  margin: 0;
}

/* Section titles */
.mw-section-title {
  text-align: center;
  font-size: 2rem;
  font-weight: 800;
  margin: 0 0 12px;
  color: var(--mw-text);
  letter-spacing: -0.01em;
}
.mw-section-subtitle {
  text-align: center;
  font-size: 1.05rem;
  color: var(--mw-text-muted);
  margin: 0 0 36px;
}

/* How It Works */
.mw-steps {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 32px;
  margin-bottom: 48px;
}
.mw-step {
  text-align: center;
  position: relative;
}
.mw-step-number {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: var(--mw-hero-gradient);
  color: #ffffff;
  font-size: 1.25rem;
  font-weight: 800;
  margin-bottom: 16px;
}
.mw-step h3 {
  font-size: 1.1rem;
  font-weight: 700;
  margin: 0 0 8px;
  color: var(--mw-text);
}
.mw-step p {
  font-size: 0.925rem;
  color: var(--mw-text-muted);
  margin: 0;
}

/* Final CTA */
.mw-cta {
  background: var(--mw-bg-alt);
  border: 1px solid var(--mw-card-border);
  border-radius: var(--mw-radius);
  padding: 48px 32px;
  text-align: center;
  margin-bottom: 24px;
}
.mw-cta h2 {
  font-size: 1.75rem;
  font-weight: 800;
  margin: 0 0 12px;
  color: var(--mw-text);
}
.mw-cta p {
  font-size: 1.05rem;
  color: var(--mw-text-muted);
  margin: 0 0 24px;
}
.mw-btn-cta {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 14px 32px;
  border-radius: 8px;
  font-size: 1rem;
  font-weight: 600;
  text-decoration: none;
  background: var(--mw-primary);
  color: #ffffff;
  transition: transform 0.15s, box-shadow 0.15s, background 0.15s;
}
.mw-btn-cta:hover {
  background: var(--mw-primary-light);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(79,70,229,0.3);
}

@media (max-width: 640px) {
  .mw-hero { padding: 48px 20px; }
  .mw-hero h1 { font-size: 2rem; }
  .mw-hero .mw-subtitle { font-size: 1.05rem; }
  .mw-section-title { font-size: 1.5rem; }
}
</style>

<div class="mw-landing">

<!-- Hero -->
<div class="mw-hero">
  <h1>Your Knowledge, Connected</h1>
  <p class="mw-subtitle">
    MeshWeaver brings together documents, collaboration, and AI-powered assistance into one unified platform for your team.
  </p>
  <div class="mw-hero-actions">
    <a class="mw-btn mw-btn-primary" href="/login">&#x1F511; Sign In</a>
    <a class="mw-btn mw-btn-secondary" href="#features">&#x2193; Learn More</a>
  </div>
</div>

<!-- Features -->
<h2 class="mw-section-title" id="features">Everything You Need</h2>
<p class="mw-section-subtitle">Powerful tools to organize, collaborate, and accelerate your work</p>

<div class="mw-features">
  <div class="mw-feature-card">
    <span class="mw-feature-icon">&#x1F4C2;</span>
    <h3>Organize</h3>
    <p>Structure documents, code, and data in a flexible graph hierarchy. Find anything instantly with powerful search.</p>
  </div>
  <div class="mw-feature-card">
    <span class="mw-feature-icon">&#x1F91D;</span>
    <h3>Collaborate</h3>
    <p>Comment on documents, request approvals, and track changes in real time. Keep everyone aligned.</p>
  </div>
  <div class="mw-feature-card">
    <span class="mw-feature-icon">&#x1F4AC;</span>
    <h3>AI Chat</h3>
    <p>Get instant answers, generate content, and automate workflows with built-in AI assistants.</p>
  </div>
  <div class="mw-feature-card">
    <span class="mw-feature-icon">&#x1F514;</span>
    <h3>Notifications</h3>
    <p>Stay informed with real-time alerts for approval requests, decisions, mentions, and updates.</p>
  </div>
</div>

<!-- How It Works -->
<h2 class="mw-section-title">Get Started in 3 Steps</h2>
<p class="mw-section-subtitle">From sign-up to productivity in minutes</p>

<div class="mw-steps">
  <div class="mw-step">
    <div class="mw-step-number">1</div>
    <h3>Sign In</h3>
    <p>Use your Microsoft, Google, LinkedIn, or Apple account to get started — no new passwords to remember.</p>
  </div>
  <div class="mw-step">
    <div class="mw-step-number">2</div>
    <h3>Set Up Your Profile</h3>
    <p>Tell us a bit about yourself so your team can find and collaborate with you easily.</p>
  </div>
  <div class="mw-step">
    <div class="mw-step-number">3</div>
    <h3>Start Exploring</h3>
    <p>Access your dashboard with recent activity, notifications, and AI-powered tools at your fingertips.</p>
  </div>
</div>

<!-- Final CTA -->
<div class="mw-cta">
  <h2>Ready to Get Started?</h2>
  <p>Join your team on MeshWeaver and take your productivity to the next level.</p>
  <a class="mw-btn-cta" href="/login">&#x1F680; Sign In Now</a>
</div>

</div>
