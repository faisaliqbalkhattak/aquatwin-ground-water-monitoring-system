"""Run the complete AquaTwin backend with `python -m aquatwin_backend`."""

from __future__ import annotations

import os

import uvicorn


def main() -> None:
    uvicorn.run(
        "aquatwin_backend.api:app",
        host=os.getenv("AQUATWIN_API_HOST", "127.0.0.1"),
        port=int(os.getenv("AQUATWIN_API_PORT", "8000")),
        reload=False,
    )


if __name__ == "__main__":
    main()
