CREATE TABLE IF NOT EXISTS public.user_sessions (
 session_id UUID PRIMARY KEY,
 user_id INTEGER NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
 token_hash VARCHAR(64) NOT NULL UNIQUE,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 expires_at TIMESTAMPTZ NOT NULL,
 revoked_at TIMESTAMPTZ NULL,
 last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 CONSTRAINT user_sessions_expiry_after_creation CHECK(expires_at>created_at)
);
CREATE INDEX IF NOT EXISTS ix_user_sessions_user_active ON public.user_sessions(user_id,created_at DESC) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_user_sessions_expiry ON public.user_sessions(expires_at);

