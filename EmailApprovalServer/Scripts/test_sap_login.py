import ssl
import xml.etree.ElementTree as ET
import urllib.request
import urllib.parse

CONFIG_PATH = 'Config/appsettings.xml'

def load_config():
    tree = ET.parse(CONFIG_PATH)
    root = tree.getroot()
    return {
        'service_layer_url': root.findtext('./Sap/ServiceLayerUrl', default=''),
        'company_db': root.findtext('./Sap/CompanyDb', default=''),
        'username': root.findtext('./Sap/UserName', default=''),
        'password': root.findtext('./Sap/Password', default=''),
    }

if __name__ == '__main__':
    cfg = load_config()
    import json
    url = cfg['service_layer_url'].rstrip('/') + '/Login'
    data = json.dumps({
        'UserName': cfg['username'],
        'Password': cfg['password'],
        'CompanyDB': cfg['company_db'],
    }).encode('utf-8')
    headers = {
        'Content-Type': 'application/json'
    }
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    req = urllib.request.Request(url, data=data, headers=headers, method='POST')
    print('URL:', url)
    print('Payload:', data)
    try:
        with urllib.request.urlopen(req, context=context, timeout=30) as resp:
            print('Status:', resp.status)
            print('Headers:')
            for k, v in resp.getheaders():
                print(f'  {k}: {v}')
            body = resp.read().decode('utf-8', errors='replace')
            print('Body:')
            print(body)
    except urllib.error.HTTPError as e:
        print('HTTPError:', e.code, e.reason)
        body = e.read().decode('utf-8', errors='replace')
        print('Body:')
        print(body)
    except Exception as e:
        print('Exception:', type(e).__name__, e)
