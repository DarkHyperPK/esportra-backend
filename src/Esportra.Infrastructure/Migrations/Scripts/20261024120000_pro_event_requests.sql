-- Event briefs, Pro forma invoices and private payment proofs. These records never grant entitlements.
CREATE TABLE IF NOT EXISTS public.pro_event_requests (
    id uuid PRIMARY KEY,
    reference text NOT NULL UNIQUE,
    event_name text NOT NULL CHECK (char_length(event_name) BETWEEN 3 AND 120),
    game_title text NOT NULL CHECK (char_length(game_title) BETWEEN 2 AND 80),
    email text NOT NULL CHECK (char_length(email) <= 254),
    starts_on date NOT NULL,
    ends_on date NOT NULL CHECK (ends_on >= starts_on),
    team_count integer NOT NULL CHECK (team_count BETWEEN 2 AND 10000),
    estimated_amount integer CHECK (estimated_amount > 0),
    payload_hash text NOT NULL CHECK (char_length(payload_hash) = 64),
    access_token_hash text NOT NULL CHECK (char_length(access_token_hash) = 64),
    invoice_details jsonb,
    invoice_expires_at timestamptz,
    proof_image bytea CHECK (octet_length(proof_image) <= 5242880),
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'contacted', 'closed', 'proof_submitted')),
    handled_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_pro_event_requests_queue ON public.pro_event_requests (status, created_at, id);

CREATE TABLE IF NOT EXISTS public.pro_event_request_audit (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id uuid NOT NULL REFERENCES public.pro_event_requests(id),
    actor_id uuid NOT NULL,
    status text NOT NULL CHECK (status IN ('pending', 'contacted', 'closed', 'proof_submitted')),
    created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE public.pro_event_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.pro_event_request_audit ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.pro_event_requests, public.pro_event_request_audit FROM PUBLIC;
-- No client-role policies: personal event briefs are available only through the authorized API.

-- Values are deliberately blank and invoicing is off until staff configures verified business details.
INSERT INTO public.system_settings (key, value, category, label, description, data_type, is_sensitive)
VALUES
 ('pro_event.enabled', 'false', 'pro_event_payments', 'Enable instant Pro invoicing', 'Enable only after verifying the business payee details below.', 'boolean', false),
 ('pro_event.seller_name', '', 'pro_event_payments', 'Seller legal name', 'Shown on Pro forma invoices.', 'string', false),
 ('pro_event.bank_name', '', 'pro_event_payments', 'Bank or payment provider', 'Verified business payment destination.', 'string', false),
 ('pro_event.account_title', '', 'pro_event_payments', 'Account title', 'Verified business payee name.', 'string', false),
 ('pro_event.account_number', '', 'pro_event_payments', 'Account number / IBAN', 'Public payment instructions, not a credential.', 'string', false),
 ('pro_event.instructions', '', 'pro_event_payments', 'Payment instructions', 'Include any supported transfer methods and reference instructions.', 'string', false),
 ('pro_event.qr_image_url', '', 'pro_event_payments', 'Verified payment QR image URL', 'Optional HTTPS image of the actual business payment QR. No QR is generated from unverified data.', 'url', false)
ON CONFLICT (key) DO NOTHING;
