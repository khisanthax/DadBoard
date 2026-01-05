# DadVoice Mumble Server (Docker)

## Overview
This stack runs a self-hosted Mumble server using Docker. It exposes TCP and UDP on port 64738 and stores data in a named volume.

## Portainer stack deployment
1. Open Portainer and go to Stacks.
2. Click Add stack and name it `dadvoice`.
3. Paste the contents of `server/docker-compose.yml`.
4. Update the environment values:
   - `MUMBLE_SERVER_PASSWORD` - shared server password for the family
   - `MUMBLE_WELCOMETEXT` - short welcome banner
5. Deploy the stack.

## Notes
- Ports: 64738/TCP and 64738/UDP must be open from your LAN to the server host.
- Persistent data lives in the `mumble-data` volume.
- If your Mumble image version uses different environment names, map them to the Murmur settings keys `serverpassword` and `welcometext`.
