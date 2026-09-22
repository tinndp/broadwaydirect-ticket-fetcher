"""Paciolan eVenue (`*.evenue.net`) fetcher - same shape as the sibling
`broadwaydirect`/`stubhub` packages: a `patchright`-driven client past the
site's bot wall (PerimeterX here), grouping into the shared
price_levels/listings contract, mirrored into MongoDB in the REAL .NET
Rowing staging shape (one SHARED `PaciolanEvenue_Inventories_NEW`
collection for the whole datasource, keyed by SourceEventId, one document
per listing) - see mongo_inventory.py's module docstring for why this does
NOT use broadwaydirect/stubhub's own raw_events/cleaned_events collections,
and for a 2026-09-22 correction to the collection-naming claim made here.

See README.md in this folder for the full picture, and
EVENUE_PERIMETERX_FINDINGS.md for what was tried before this package existed
and why it's built the way it is.
"""
