CREATE INDEX ix_query_listing_category_search
    ON documents.listing_category
    (base_projection_id, category_key, listing_id);

CREATE INDEX ix_query_listing_district_search
    ON documents.listing_geography
    (base_projection_id, district_key, listing_id)
    WHERE district_key IS NOT NULL;

CREATE INDEX ix_query_listing_kind_search
    ON documents.listing_document
    (base_projection_id, listing_kind, listing_id);

CREATE INDEX ix_query_listing_contact_kind_search
    ON documents.listing_contact
    (base_projection_id, kind, listing_id);

CREATE INDEX ix_query_promotion_overlay_scope_search
    ON projection.promotion_overlay_item
    (overlay_id, scope_type, scope_key, placement_id);
