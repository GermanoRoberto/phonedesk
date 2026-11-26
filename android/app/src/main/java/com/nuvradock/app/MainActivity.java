package com.nuvradock.app;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.TextView;

public class MainActivity extends Activity {
    private WebView webView;
    private TextView loadingOverlay;
    private static final String TARGET_USB_URL = "http://127.0.0.1:8770/";
    private String activeUrl = TARGET_USB_URL;
    private boolean isRetrying = false;
    private boolean hasLoadedPage = false;
    private boolean lastLoadHadError = false;

    public class PhoneDeskInterface {
        @android.webkit.JavascriptInterface
        public void setBrightness(final float brightness) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    try {
                        WindowManager.LayoutParams lp = getWindow().getAttributes();
                        lp.screenBrightness = Math.max(0.01f, Math.min(1.0f, brightness));
                        getWindow().setAttributes(lp);
                    } catch (Exception e) {}
                }
            });
        }

        @android.webkit.JavascriptInterface
        public void resetBrightness() {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    try {
                        WindowManager.LayoutParams lp = getWindow().getAttributes();
                        lp.screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE;
                        getWindow().setAttributes(lp);
                    } catch (Exception e) {}
                }
            });
        }
    }

    private static boolean isTrustedPanelUrl(String url) {
        if (url == null) return false;
        try {
            Uri uri = Uri.parse(url);
            String scheme = uri.getScheme();
            String host = uri.getHost();
            if (scheme == null || host == null) return false;
            if (!"http".equalsIgnoreCase(scheme) && !"https".equalsIgnoreCase(scheme)) return false;
            return "127.0.0.1".equals(host)
                || "localhost".equalsIgnoreCase(host)
                || host.startsWith("192.168.")
                || host.startsWith("10.")
                || host.startsWith("100.")
                || host.endsWith(".local");
        } catch (Exception e) {
            return false;
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        webView = new WebView(this);
        webView.setBackgroundColor(Color.BLACK);
        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        webView.setVerticalScrollBarEnabled(false);
        webView.setHorizontalScrollBarEnabled(false);
        webView.setVisibility(View.INVISIBLE);

        WebSettings ws = webView.getSettings();
        ws.setJavaScriptEnabled(true);
        ws.setDomStorageEnabled(true);
        ws.setDatabaseEnabled(true);
        // Hardened: never allow file:// or content:// access inside WebView
        ws.setAllowFileAccess(false);
        ws.setAllowContentAccess(false);
        ws.setLoadWithOverviewMode(true);
        ws.setUseWideViewPort(true);
        ws.setMediaPlaybackRequiresUserGesture(false);
        ws.setCacheMode(WebSettings.LOAD_DEFAULT);

        PhoneDeskInterface bridge = new PhoneDeskInterface();
        webView.setWebChromeClient(new WebChromeClient());
        webView.addJavascriptInterface(bridge, "NuvraDockNative");
        webView.addJavascriptInterface(bridge, "PhoneDeskNative");

        // Native overlay covers WebView completely until first clean HTTP load (hides Android error robot forever)
        loadingOverlay = new TextView(this);
        loadingOverlay.setBackgroundColor(Color.BLACK);
        loadingOverlay.setTextColor(Color.parseColor("#8E8E93"));
        loadingOverlay.setTextSize(16f);
        loadingOverlay.setGravity(Gravity.CENTER);
        loadingOverlay.setText("Nuvra Dock · Conectando…");

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                String url = request != null && request.getUrl() != null ? request.getUrl().toString() : null;
                if (isTrustedPanelUrl(url)) {
                    view.loadUrl(url);
                }
                return true;
            }

            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                if (isTrustedPanelUrl(url)) {
                    view.loadUrl(url);
                }
                return true;
            }

            @Override
            public void onPageStarted(WebView view, String url, android.graphics.Bitmap favicon) {
                super.onPageStarted(view, url, favicon);
                if (isTrustedPanelUrl(url)) {
                    lastLoadHadError = false;
                }
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                if (!lastLoadHadError && isTrustedPanelUrl(url)) {
                    hasLoadedPage = true;
                    webView.setVisibility(View.VISIBLE);
                    loadingOverlay.setVisibility(View.GONE);
                }
            }

            @Override
            public void onReceivedError(WebView view, int errorCode, String description, String failingUrl) {
                lastLoadHadError = true;
                handleLoadError();
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request != null && request.isForMainFrame()) {
                    lastLoadHadError = true;
                    handleLoadError();
                }
            }
        });

        root.addView(webView, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ));
        root.addView(loadingOverlay, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ));

        setContentView(root);
        hideSystemUI();

        String intentUrl = extractTrustedIntentUrl(getIntent());
        if (intentUrl != null) activeUrl = intentUrl;
        webView.loadUrl(activeUrl);
    }

    private static String extractTrustedIntentUrl(Intent intent) {
        if (intent == null) return null;
        String candidate = null;
        if (intent.hasExtra("url")) candidate = intent.getStringExtra("url");
        else if (intent.getData() != null) candidate = intent.getData().toString();
        return isTrustedPanelUrl(candidate) ? candidate : null;
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        String u = extractTrustedIntentUrl(intent);
        if (u != null && webView != null) {
            activeUrl = u;
            hasLoadedPage = false;
            loadingOverlay.setVisibility(View.VISIBLE);
            webView.loadUrl(activeUrl);
        } else if (!hasLoadedPage && !isRetrying && webView != null) {
            webView.loadUrl(activeUrl);
        }
    }

    private void handleLoadError() {
        if (hasLoadedPage) return;
        webView.setVisibility(View.INVISIBLE);
        loadingOverlay.setVisibility(View.VISIBLE);
        if (isRetrying) return;
        isRetrying = true;
        webView.postDelayed(new Runnable() {
            @Override
            public void run() {
                isRetrying = false;
                if (!hasLoadedPage && webView != null) {
                    webView.loadUrl(activeUrl);
                }
            }
        }, 2000);
    }

    @Override
    protected void onResume() {
        super.onResume();
        hideSystemUI();
        if (!hasLoadedPage && !isRetrying && webView != null) {
            webView.loadUrl(activeUrl);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemUI();
    }

    private void hideSystemUI() {
        View decor = getWindow().getDecorView();
        decor.setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_FULLSCREEN
        );
    }

    @Override
    public void onBackPressed() {
        if (webView != null && webView.canGoBack()) {
            webView.goBack();
        }
    }
}
