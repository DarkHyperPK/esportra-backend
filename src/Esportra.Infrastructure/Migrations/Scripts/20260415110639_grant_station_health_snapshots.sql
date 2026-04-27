-- Grant permissions on station_health_snapshots for sync service
GRANT ALL ON station_health_snapshots TO service_role;
GRANT SELECT, INSERT ON station_health_snapshots TO authenticated;
