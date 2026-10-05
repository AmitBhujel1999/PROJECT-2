from django.apps import AppConfig


class CommonConfig(AppConfig):
    name = "apps.common"
    label = "common"
    verbose_name = "Common / Company settings"

    def ready(self):
        from . import companies  # noqa: F401  (connects the connection_created handler)
