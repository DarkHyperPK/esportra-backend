namespace Esportra.Contracts.Requests;

public sealed record SponsorUpdateRequest(
    string?   Name            = null,
    string?   Tagline         = null,
    string?   Description     = null,
    string?   WebsiteUrl      = null,
    string?   CtaText         = null,
    string?   LogoUrl         = null,
    string?   BannerImageUrl  = null,
    string[]? GalleryImages   = null);

public sealed record OnboardingStepRequest(
    string                            StepName,
    Dictionary<string, object?>       StepData,
    int                               NextStep);

public sealed record OnboardingCompleteRequest(
    string AgreedAt,
    string Ip);

public sealed record TrackSponsorImpressionRequest(
    string SponsorId,
    string EventType);
