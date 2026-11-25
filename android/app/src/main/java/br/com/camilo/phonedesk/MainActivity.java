package br.com.camilo.phonedesk;

import android.app.Activity;
import android.os.Bundle;
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
import android.graphics.Color;
import android.content.pm.ActivityInfo;

public class MainActivity extends Activity {
    private WebView webView;
    private static final String TARGET_LAN_URL = "http://192.168.101.72:8770/";
    private static final String TARGET_USB_URL = "http://127.0.0.1:8770/";
    private boolean useUsb = false;
    private boolean isRetrying = false;
    private boolean hasLoadedPage = false;

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

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Orientação horizontal pura para Smart Desk Panel
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);

        // Tela cheia imersiva e sem desligar (Always-On Desk Panel)
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().setFormat(android.graphics.PixelFormat.TRANSLUCENT);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.parseColor("#000000"));

        webView = new WebView(this);
        webView.setBackgroundColor(Color.parseColor("#000000"));
        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        webView.setVerticalScrollBarEnabled(false);
        webView.setHorizontalScrollBarEnabled(false);

        WebSettings ws = webView.getSettings();
        ws.setJavaScriptEnabled(true);
        ws.setDomStorageEnabled(true);
        ws.setDatabaseEnabled(true);
        ws.setAllowFileAccess(true);
        ws.setAllowContentAccess(true);
        ws.setLoadWithOverviewMode(true);
        ws.setUseWideViewPort(true);
        ws.setMediaPlaybackRequiresUserGesture(false);
        ws.setCacheMode(WebSettings.LOAD_DEFAULT);
        ws.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);

        webView.setWebChromeClient(new WebChromeClient());
        webView.addJavascriptInterface(new PhoneDeskInterface(), "PhoneDeskNative");
        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                view.loadUrl(url);
                return true;
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                hasLoadedPage = true;
            }

            @Override
            public void onReceivedError(WebView view, int errorCode, String description, String failingUrl) {
                handleLoadError();
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) {
                    handleLoadError();
                }
            }
        });

        root.addView(webView, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT
        ));

        setContentView(root);
        hideSystemUI();

        webView.loadUrl(TARGET_LAN_URL);
    }

    private void handleLoadError() {
        // Se a página já foi carregada alguma vez, NÃO substitua a página pelo erro do navegador.
        // O JavaScript local manterá o relógio na tela como um desk clock autônomo.
        if (hasLoadedPage) return;
        if (isRetrying) return;
        isRetrying = true;
        webView.postDelayed(new Runnable() {
            @Override
            public void run() {
                isRetrying = false;
                useUsb = !useUsb;
                webView.loadUrl(useUsb ? TARGET_USB_URL : TARGET_LAN_URL);
            }
        }, 3000);
    }

    @Override
    protected void onResume() {
        super.onResume();
        hideSystemUI();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            hideSystemUI();
        }
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
        // Travar saída acidental no Desk Deck: voltar para o início se possível
        if (webView != null && webView.canGoBack()) {
            webView.goBack();
        }
    }
}
