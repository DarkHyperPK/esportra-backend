CREATE TABLE public.air_production_events (
 id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES public.air_projects(id), device_id uuid NOT NULL REFERENCES public.air_devices(id),
 occurred_at timestamptz NOT NULL, event_type text NOT NULL CHECK(length(event_type) BETWEEN 1 AND 80),
 metadata jsonb NOT NULL CHECK(jsonb_typeof(metadata)='object' AND octet_length(metadata::text)<=8192), received_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX air_production_events_project ON public.air_production_events(project_id,occurred_at DESC);
ALTER TABLE public.air_production_events ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.air_production_events FROM anon,authenticated;
