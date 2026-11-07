# The page talks to the app through androidx.webkit's WebMessageListener (no reflection); nothing of ours is
# looked up by name. OkHttp and Play services bring their own rules.
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
