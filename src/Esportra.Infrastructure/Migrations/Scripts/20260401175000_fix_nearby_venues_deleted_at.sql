-- Fix find_nearby_venues to exclude soft-deleted venues
CREATE OR REPLACE FUNCTION find_nearby_venues(
  user_lat FLOAT,
  user_lng  FLOAT,
  radius_km FLOAT DEFAULT 50
)
RETURNS SETOF public.venues
LANGUAGE sql STABLE AS $$
  SELECT v.*
  FROM public.venues v
  WHERE
    v.status = 'published'
    AND v.deleted_at IS NULL
    AND v.latitude  IS NOT NULL
    AND v.longitude IS NOT NULL
    AND (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) <= radius_km
  ORDER BY (
      6371 * acos(
        LEAST(1.0,
          cos(radians(user_lat)) * cos(radians(v.latitude)) *
          cos(radians(v.longitude) - radians(user_lng)) +
          sin(radians(user_lat)) * sin(radians(v.latitude))
        )
      )
    ) ASC;
$$;
