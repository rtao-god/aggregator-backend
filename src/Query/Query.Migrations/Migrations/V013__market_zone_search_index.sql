CREATE INDEX ix_query_listing_market_zone_search
    ON documents.listing_geography
    (base_projection_id, state, listing_id);
