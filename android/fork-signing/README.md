# Fork signing

`uber-debug.keystore` is the Android **debug** keystore that ships inside
[uber-apk-signer](https://github.com/patrickfav/uber-apk-signer) (`debug.keystore` in its jar).
Its password and alias are the standard debug ones (`android` / `androiddebugkey`), so it is public:
anybody who downloads that tool has the same file.

The fork's CI signs release APKs with it because the app already installed on this project's
phones was signed with it, and Android only installs an update that carries the same key. Nothing
secret is added by committing it — it was public before this directory existed.

Trade-off: a public key cannot prove authorship. Someone who knows the package name could build
an APK with this key and ask you to install it. Only install builds that come from this
repository's Actions run.

## Using a private key instead (recommended once you stop reinstalling)

1. Create a keystore once and keep it off the repository:

   ```bash
   keytool -genkeypair -keystore phonebox-release.jks -alias phonebox \
     -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=PhoneBox, O=PhoneBox"
   base64 -w0 phonebox-release.jks    # macOS: base64 -i phonebox-release.jks
   ```

2. Add four repository secrets (Settings › Secrets and variables › Actions):
   `PHONEBOX_KEYSTORE_BASE64`, `PHONEBOX_STORE_PASSWORD`, `PHONEBOX_KEY_ALIAS`,
   `PHONEBOX_KEY_PASSWORD`.

3. `.github/workflows/release-fork.yml` prefers those secrets and only falls back to this file,
   so once they exist this keystore can be deleted.

4. The first release signed with the new key cannot install over the old app: uninstall the phone
   app once (`adb uninstall net.oxog.phonebox.deskpanel`), then install the new APK and re-pair.
