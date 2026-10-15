-- A grant may inherit its plan, or explicitly override both role allowances.
ALTER TABLE public.air_accounts ADD COLUMN override_device_limits boolean NOT NULL DEFAULT false;
ALTER TABLE public.air_accounts ADD COLUMN production_limit integer CHECK(production_limit BETWEEN 0 AND 1000);
ALTER TABLE public.air_accounts ADD COLUMN capture_limit integer CHECK(capture_limit BETWEEN 0 AND 1000);
