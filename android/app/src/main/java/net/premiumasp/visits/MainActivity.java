package net.premiumasp.visits;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Bundle;
import android.webkit.*;

public class MainActivity extends Activity {
    // غيّر الرابط لو الدومين اتغير (لازم HTTPS)
    static final String URL = "https://visits.premiumasp.net/#rep";
    WebView web;
    ValueCallback<Uri[]> chooser;

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        requestPermissions(new String[]{Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA}, 1);
        web = new WebView(this);
        setContentView(web);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setGeolocationEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        // يشبه Chrome العادي (بعض الاستضافات بتقفل الاتصال لو شافت WebView)
        s.setUserAgentString(s.getUserAgentString().replace("; wv", "").replaceAll("Version/[0-9.]+ ", ""));
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest r) {
                if ("openext".equals(r.getUrl().getScheme())) {
                    try { startActivity(new android.content.Intent(android.content.Intent.ACTION_VIEW, Uri.parse(URL))); } catch (Exception ignored) {}
                    return true;
                }
                return false;
            }
            @Override public void onReceivedHttpError(WebView v, WebResourceRequest r, WebResourceResponse e) {
                if (r.isForMainFrame() && e.getStatusCode() >= 500) showErr(v, "HTTP " + e.getStatusCode());
            }
            @Override public void onReceivedError(WebView v, WebResourceRequest r, WebResourceError e) {
                if (r.isForMainFrame()) showErr(v, e.getErrorCode() + " — " + e.getDescription());
            }
            private void showErr(WebView v, String d) {
                v.loadDataWithBaseURL(null, "<html dir='rtl'><body style='font-family:sans-serif;text-align:center;padding:40px'><h2>تعذّر الاتصال</h2><p>تأكد من الإنترنت وحاول مرة أخرى.</p><p><a href='" + URL + "' style='font-size:20px'>إعادة المحاولة</a></p><p><a href='openext://x' style='font-size:20px'>فتح في المتصفح</a></p><p dir='ltr' style='color:#888;font-size:13px'>" + d + "</p></body></html>", "text/html", "utf-8", null);
            }
        });
        web.setWebChromeClient(new WebChromeClient() {
            @Override public void onGeolocationPermissionsShowPrompt(String o, GeolocationPermissions.Callback cb) {
                cb.invoke(o, checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED, false);
            }
            @Override public void onPermissionRequest(PermissionRequest r) { runOnUiThread(() -> r.grant(r.getResources())); }
            @Override public boolean onShowFileChooser(WebView v, ValueCallback<Uri[]> cb, FileChooserParams p) {
                if (chooser != null) chooser.onReceiveValue(null);
                chooser = cb;
                try { startActivityForResult(p.createIntent(), 2); } catch (Exception e) { chooser = null; return false; }
                return true;
            }
        });
        web.loadUrl(URL);
    }

    @Override protected void onActivityResult(int req, int res, Intent d) {
        if (req == 2 && chooser != null) {
            chooser.onReceiveValue(WebChromeClient.FileChooserParams.parseResult(res, d));
            chooser = null;
        }
    }

    @Override public void onBackPressed() { if (web.canGoBack()) web.goBack(); else super.onBackPressed(); }
}
