"""Verify whole current consumer/auth sources before the test assembly compiles them."""
import hashlib
import pathlib
import sys

EXPECTED = {
    'Legacy.Maliev.Intranet.Bff/Orders/OrderCreateProxies.cs': {
        '91f3f2f9fa9416680bbbcaa9aa5f2518f2db08440a3cfd49d8fd0a275efba799',
        '59a7a5435617f550a2c0b237e5ce4c8392c04ed5d15fba1f928442552b4d884a'},
    'Legacy.Maliev.Intranet.Server/Auth/LegacyServiceAuthenticationHandler.cs': {
        'd1d3e7910f3890831a70026a24a3bf541ccc3b77b28e0c6720a92e5152eb1442',
        'd94c5018f5bc111de969fdfcc330db55a416d92894a67501247e461bb362f83e'},
    'Legacy.Maliev.Intranet.Server/Auth/ServiceAccessTokenProvider.cs': {
        'f1ed6a46897d1683a414d0f966efe1801adaf8ecea15c806b36f92ad5a1ceeac',
        'ccdb9106a1cd194aa78e626b7b72fb96f223f56b323fe4d1aad6553e262237a5'},
}


def verify(root):
    for relative, allowed in EXPECTED.items():
        path = pathlib.Path(root) / relative
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() not in allowed:
            raise ValueError('BLOCKED: immutable consumer source mismatch: ' + relative)


if __name__ == '__main__':
    verify(sys.argv[1])
    print('Verified current BFF consumer and real service-auth sources (LF or CRLF).')
