module OrcAI.Auth.Tests.TestData

open OrcAI.Auth.AuthConfig
open OrcAI.Auth.AppAuth

/// Builders for auth domain types. Use `defaults ()` for a sensible base value,
/// then pipe through `with*` helpers to express only what each test cares about.
module A =

    module ProfileEntry =
        let app appId keyPath installId : ProfileEntry =
            { Type = "app"; Token = None; AppId = Some appId; KeyPath = Some keyPath; InstallationId = Some installId }

        let pat token : ProfileEntry =
            { Type = "pat"; Token = Some token; AppId = None; KeyPath = None; InstallationId = None }

    module AuthConfig =
        let withProfiles (active: string) (profiles: (string * ProfileEntry) list) : AuthConfigFile =
            let dict = System.Collections.Generic.Dictionary<string, ProfileEntry>()
            for (name, entry) in profiles do dict.[name] <- entry
            { Active = active; Profiles = dict }

    module AppAuthConfig =
        let defaults () : AppAuthConfig =
            { AppId = "app-123"; PrivateKeyPath = "/tmp/key.pem"; InstallationId = "install-456"; PrivateKeyPem = None }

        let withAppId id (c: AppAuthConfig)          = { c with AppId = id }
        let withInstallId id (c: AppAuthConfig)      = { c with InstallationId = id }
        let withPrivateKeyPath p (c: AppAuthConfig)  = { c with PrivateKeyPath = p }
        let withPrivateKeyPem pem (c: AppAuthConfig) = { c with PrivateKeyPem = pem }
