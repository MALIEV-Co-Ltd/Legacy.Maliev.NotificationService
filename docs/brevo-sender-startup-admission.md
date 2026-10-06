# Sender configuration startup admission

Accepted Notification52 validates required provider configuration at startup, but
its outer options validation does not inspect the dictionary's sender values.
BrevoSenderOptions already declares Required/EmailAddress rules for Address and
a Required DisplayName. The .NET10 DataAnnotationValidateOptions implementation
recurses only through explicitly opted-in object/item properties; this dictionary
had no such marker. The required-channel predicate admitted all four keys even
when their values were invalid. No baseline native failure is claimed.

Normal Program now applies those existing annotations to every configured sender
before startup succeeds. Required channels are preserved. Null entries/dictionaries
fail options validation; failures use generic messages without the supplied values.
Wire payloads, provider timing/retries, credentials and auth permissions are unchanged.

Sixteen new normal Production-host tests cover blank/invalid addresses and blank
display names across four channels, incomplete/null configuration, and a valid
four-channel host retaining anonymous401 admission. Outbound provider HTTP is
controlled, with zero calls required. The null-dictionary case also controls options
construction while retaining the actual validators. Each failed-start host is explicitly disposed;
successful factories and ephemeral RSA instances use their normal disposal paths.
The missing-channel fixture removes the bound Support entry after configuration,
so base appsettings cannot supply it. The null-dictionary fixture constructs the
init-only options legally through an OptionsFactory subclass; it retains Program's
actual registered validators and the framework's validation path. No setter or
reflection workaround is added. Both defects were caught in source review before
publication; no compiler run or baseline RED is claimed.
The expected native full suite is236, retaining accepted220 plus16 new tests.
Native Release/focused/full/raw coverage/static checks remain pending.

Source provenance remains private and read-only: checkpoint
135e526d0dab85c415b3afdcefd7b70fe2c82e2f. This implements the existing typed external
sender-identity constraints; it does not copy private resource values, establish
live provider delivery, or close the whole historical75-assignment email cohort.
