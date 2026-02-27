.PHONY: dev test typecheck lint build e2e smoke up down migrate

dev:
	npm run dev

test:
	npm run test

typecheck:
	npm run typecheck

lint:
	npm run lint

build:
	npm run build

e2e:
	npm run test:e2e

smoke:
	npm run smoke:compose

up:
	docker compose -f infra/docker-compose.yml up -d

down:
	docker compose -f infra/docker-compose.yml down -v --remove-orphans

migrate:
	npm run migrate
