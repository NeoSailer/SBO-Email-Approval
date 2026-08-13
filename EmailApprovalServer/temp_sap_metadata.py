import ssl
import urllib.request

url = 'https://TISSIN:50000/b1s/v1/$metadata'
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
req = urllib.request.Request(url, headers={'Accept': 'application/xml'})
with urllib.request.urlopen(req, context=ctx, timeout=30) as r:
    xml = r.read().decode('utf-8', errors='replace')
    print(xml[:8000])
